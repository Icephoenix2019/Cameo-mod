#region Copyright & License Information
/*
 * Written by Boolbada of OP Mod.
 * Follows OpenRA's license, GPLv3 as follows:
 *
 * Copyright 2007-2018 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.CA.Traits
{
	// What to do when master is killed or mind controlled
	public enum MobMemberDisposal
	{
		DoNothing,
		KillSlaves,
		GiveSlavesToAttacker
	}

	[Desc("This actor can spawn actors.")]
	public class MobSpawnerMasterInfo : BaseSpawnerMasterBInfo
	{
		[Desc("Spawn at a member, not the nexus?")]
		public readonly bool ExitByBudding = true;

		[Desc("Can the slaves be controlled independently?")]
		public readonly bool SlavesHaveFreeWill = false;

		[Desc("This is a dummy spawner like in C&C Generals and use virtual position and health.")]
		public readonly bool AggregateHealth = true;

		[Desc("Spawn actors with this offset to nexus.")]
		public readonly WVec Offset = WVec.Zero;

		public readonly int AggregateHealthUpdateDelay = 17; // Just a visual parameter, Doesn't affect the game.

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);

			if (Actors == null || Actors.Length == 0)
				throw new YamlException("Actors is null or empty for MobSpawner for actor type {0}!".F(ai.Name));

			if (InitialActorCount > Actors.Length || InitialActorCount < -1)
				throw new YamlException("MobSpawner can't have more InitialActorCount than the actors defined!");

			if (InitialActorCount == 0 && AggregateHealth == true)
				throw new YamlException("You can't have InitialActorCount == 0 and AggregateHealth");
		}

		public override object Create(ActorInitializer init) { return new MobSpawnerMaster(init, this); }
	}

	public class MobSpawnerMaster : BaseSpawnerMasterB, INotifyCreated, INotifyOwnerChanged, ITick, IResolveOrder, INotifyAttack
	{
		class MobSpawnerSlaveEntry : BaseSpawnerSlaveEntryB
		{
			public new MobSpawnerSlave SpawnerSlave;
			public Health Health;
		}

		public new MobSpawnerMasterInfo Info { get; private set; }

		MobSpawnerSlaveEntry[] slaveEntries;
		ConditionManager conditionManager;

		// bool hasSpawnedInitialLoad = false;
		int spawnReplaceTicks = 0;

		IPositionable position;
		Aircraft aircraft;
		Health health;

		public MobSpawnerMaster(ActorInitializer init, MobSpawnerMasterInfo info)
			: base(init, info)
		{
			Info = info;
		}

		protected override void Created(Actor self)
		{
			position = self.TraitOrDefault<IPositionable>();
			health = self.Trait<Health>();
			aircraft = self.TraitOrDefault<Aircraft>();

			base.Created(self); // Base class does the initial spawning
			conditionManager = self.Trait<ConditionManager>();

			// Spawn initial load.
			var burst = Info.InitialActorCount == -1 ? Info.Actors.Length : Info.InitialActorCount;
			for (var i = 0; i < burst; i++)
				Replenish(self, SlaveEntries);

			if (!IsTraitDisabled)
				SpawnReplenishedSlaves(self);
		}

		protected override void TraitEnabled(Actor self)
		{
			base.TraitEnabled(self);

			SpawnReplenishedSlaves(self);
		}

		public override BaseSpawnerSlaveEntryB[] CreateSlaveEntries(BaseSpawnerMasterBInfo info)
		{
			slaveEntries = new MobSpawnerSlaveEntry[info.Actors.Length]; // For this class to use

			for (int i = 0; i < slaveEntries.Length; i++)
				slaveEntries[i] = new MobSpawnerSlaveEntry();

			return slaveEntries; // For the base class to use
		}

		public override void InitializeSlaveEntry(Actor slave, BaseSpawnerSlaveEntryB entry)
		{
			var se = entry as MobSpawnerSlaveEntry;
			base.InitializeSlaveEntry(slave, se);

			se.SpawnerSlave = slave.Trait<MobSpawnerSlave>();
			se.Health = slave.Trait<Health>();
		}

		public void ResolveOrder(Actor self, Order order)
		{
			if (Info.SlavesHaveFreeWill)
				return;

			switch (order.OrderString)
			{
				case "Stop":
					StopSlaves();
					break;
				case "Attack":
					// Game.Debug("Attack");
					AssignTargetsToSlaves(self, order.Target);
					break;
				case "ForceAttack":
					// Game.Debug("ForceAttack");
					AssignTargetsToSlaves(self, order.Target);
					break;
				default:
					// Game.Debug(order.ToString());
					break;
			}
		}

		void INotifyAttack.PreparingAttack(Actor self, Target target, Armament a, Barrel barrel)
		{
		}

		void INotifyAttack.Attacking(Actor self, Target target, Armament a, Barrel barrel)
		{
			if (Info.SlavesHaveFreeWill)
				return;

			AssignTargetsToSlaves(self, target);
		}

		void ITick.Tick(Actor self)
		{
			if (!IsTraitDisabled && !IsTraitPaused)
			{
				spawnReplaceTicks--;

				// Time to respawn someting.
				if (spawnReplaceTicks <= 0)
				{
					Replenish(self, slaveEntries);

					SpawnReplenishedSlaves(self);

					spawnReplaceTicks = OpenRA.Mods.Common.Util.ApplyPercentageModifiers(Info.RespawnTicks, reloadModifiers.Select(rm => rm.GetReloadModifier()));
				}
			}

			// I'm a virtual mob spawning nexus.
			if (Info.AggregateHealth)
			{
				SetNexusPosition(self);
				SetNexusHealth(self);
			}

			if (!Info.SlavesHaveFreeWill)
				AssignSlaveActivity(self);
		}

		void SpawnReplenishedSlaves(Actor self)
		{
			if (self.IsDead || !self.IsInWorld)
				return;

			var centerPosition = self.CenterPosition;
			if (Info.ExitByBudding)
			{
				// Spawning from a virtual nexus: exit by an existing member.
				var se = slaveEntries.FirstOrDefault(s => s.IsValid && s.Actor.IsInWorld);
				if (se != null)
					centerPosition = se.Actor.CenterPosition;
			}

			foreach (var se in slaveEntries)
			{
				if (se.IsValid && !se.Actor.IsInWorld)
					SpawnIntoWorld(self, se.Actor, centerPosition + Info.Offset);
			}

			spawnReplaceTicks = OpenRA.Mods.Common.Util.ApplyPercentageModifiers(Info.RespawnTicks, reloadModifiers.Select(rm => rm.GetReloadModifier()));
		}

		public override void SpawnIntoWorld(Actor self, Actor slave, WPos centerPosition)
		{
			var exit = self.RandomExitOrDefault(self.World, null);
			SetSpawnedFacing(slave, self, null);

			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead)
					return;

				var spawnOffset = exit == null ? WVec.Zero : exit.Info.SpawnOffset;
				slave.Trait<IPositionable>().SetPosition(slave, centerPosition + spawnOffset);

				var location = self.World.Map.CellContaining(centerPosition + spawnOffset);

				w.Add(slave);
				var mobile = slave.TraitOrDefault<Mobile>();
				if (mobile != null)
					mobile.Nudge(slave);
			});
		}

		public override void OnSlaveKilled(Actor self, Actor slave)
		{
			// No need to update mobs entry because Actor.IsDead marking is done automatically by the engine.
			// However, we need to check if all are dead when AggregateHealth.
			if (Info.AggregateHealth && slaveEntries.All(m => !m.IsValid))
				self.Dispose();

			if (spawnReplaceTicks <= 0)
				spawnReplaceTicks = Info.RespawnTicks;
		}

		void AssignTargetsToSlaves(Actor self, Target target)
		{
			foreach (var se in slaveEntries)
			{
				if (!se.IsValid)
					continue;

				se.SpawnerSlave.Attack(se.Actor, target);
			}
		}

		void MoveSlaves(Actor self)
		{
			var targets = self.CurrentActivity.GetTargets(self);
			if (!targets.Any())
				return;

			var location = self.World.Map.CellContaining(targets.First().CenterPosition);

			foreach (var se in slaveEntries)
			{
				if (!se.IsValid || !se.Actor.IsInWorld)
					continue;

				if (se.SpawnerSlave.IsFlying())
				{
					se.SpawnerSlave.Stop(se.Actor);
					se.SpawnerSlave.Move(se.Actor, location);
				}

				if (se.Actor.Location == location)
					continue;

				if (!se.SpawnerSlave.IsMoving())
				{
					se.SpawnerSlave.Stop(se.Actor);
					se.SpawnerSlave.Move(se.Actor, location);
				}
			}
		}

		CPos lastAttackMoveLocation;
		void AttackMoveSlaves(Actor self)
		{
			var targets = self.CurrentActivity.GetTargets(self);
			if (!targets.Any())
				return;

			var location = self.World.Map.CellContaining(targets.First().CenterPosition);

			if (lastAttackMoveLocation == location)
				return;

			lastAttackMoveLocation = location;

			foreach (var se in slaveEntries)
			{
				if (!se.IsValid || !se.Actor.IsInWorld)
					continue;

				se.SpawnerSlave.AttackMove(se.Actor, location);
			}
		}

		void SetNexusPosition(Actor self)
		{
			int x = 0, y = 0, cnt = 0;
			foreach (var se in slaveEntries)
			{
				if (!se.IsValid || !se.Actor.IsInWorld)
					continue;

				var pos = se.Actor.CenterPosition;
				x += pos.X;
				y += pos.Y;
				cnt++;
			}

			if (cnt == 0)
				return;

			var newPos = new WPos(x / cnt, y / cnt, aircraft != null ? aircraft.Info.CruiseAltitude.Length : 0);
			if (aircraft == null)
				position.SetPosition(self, newPos); // breaks arrival detection of the aircraft if we set position.

			position.SetVisualPosition(self, newPos);
		}

		int aggregateHealthUpdateTicks = 0;

		void SetNexusHealth(Actor self)
		{
			if (!Info.AggregateHealth)
				return;

			if (aggregateHealthUpdateTicks > 0)
			{
				aggregateHealthUpdateTicks--;
				return;
			}

			aggregateHealthUpdateTicks = Info.AggregateHealthUpdateDelay;

			// Time to aggregate health.
			int maxHealth = 0;
			int h = 0;

			foreach (var se in slaveEntries)
			{
				maxHealth += se.Health.MaxHP;

				if (!se.IsValid)
					continue;

				h += se.Health.HP;
			}

			// Apply the aggregate health.
			h = h * health.MaxHP / maxHealth;

			if (h > 0)
			{
				// Only do these when h > 0.
				// Nexus kill when wiped out is handled else where.
				// We can't set health. Inflict damage instead.
				health.InflictDamage(self, self, new Damage(-health.MaxHP), true); // fully heal
				health.InflictDamage(self, self, new Damage(health.MaxHP - h), true); // remove some health
			}
		}

		void AssignSlaveActivity(Actor self)
		{
			if (self.CurrentActivity != null)
			{
				// Game.Debug(self.CurrentActivity.ToString());
			}
			else
			{
				return;
			}

			if (self.CurrentActivity is Move || self.CurrentActivity is Fly)
			{
				MoveSlaves(self);

				// Game.Debug("Move ||Fly");
			}
			else if (self.CurrentActivity is AttackMoveActivity)
			{
				AttackMoveSlaves(self);

				// Game.Debug("AttackMoveActivity");
			}

			if (self.CurrentActivity is AttackOmni.SetTarget)
			{
				AssignTargetsToSlaves(self, self.CurrentActivity.GetTargets(self).First());

				// Game.Debug("AttackOmni.SetTarget");
			}
		}
	}
}
