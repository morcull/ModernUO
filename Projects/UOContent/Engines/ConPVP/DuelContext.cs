using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Server.Engines.PartySystem;
using Server.Factions;
using Server.Gumps;
using Server.Items;
using Server.Mobiles;
using Server.Network;
using Server.Regions;
using Server.Spells;
using Server.Spells.Bushido;
using Server.Spells.Chivalry;
using Server.Spells.Fourth;
using Server.Spells.Necromancy;
using Server.Spells.Ninjitsu;
using Server.Spells.Second;
using Server.Spells.Seventh;
using Server.Spells.Spellweaving;
using Server.Targeting;

namespace Server.Engines.ConPVP
{
  public delegate void CountdownCallback(int count);

  public class DuelContext
  {
    private static readonly TimeSpan CombatDelay = TimeSpan.FromSeconds(30.0);
    private static readonly TimeSpan AutoTieDelay = TimeSpan.FromMinutes(15.0);

    private Timer m_AutoTieTimer;

    private Timer m_Countdown;

    public EventGame m_EventGame;
    private Map m_GateFacet;

    private Point3D m_GatePoint;
    public TourneyMatch m_Match;

    public Arena m_OverrideArena;

    private Timer m_SDWarnTimer, m_SDActivateTimer;
    public Tournament m_Tournament;

    private readonly List<Item> m_Walls = new List<Item>();

    private bool m_Yielding;

    public DuelContext(Mobile initiator, RulesetLayout layout, bool addNew = true)
    {
      Initiator = initiator;
      Participants = new List<Participant>();
      Ruleset = new Ruleset(layout);
      Ruleset.ApplyDefault(layout.Defaults[0]);

      if (addNew)
      {
        Participants.Add(new Participant(this, 1));
        Participants.Add(new Participant(this, 1));
        Participants[0].Add(initiator);
      }
    }

    public bool Rematch { get; private set; }

    public bool ReadyWait { get; private set; }

    public int ReadyCount { get; private set; }

    public bool Registered { get; private set; } = true;

    public bool Finished { get; private set; }

    public bool Started { get; private set; }

    public Mobile Initiator { get; }

    public List<Participant> Participants { get; }

    public Ruleset Ruleset { get; private set; }

    public Arena Arena { get; private set; }

    public bool Tied { get; private set; }

    public bool IsSuddenDeath { get; set; }

    public bool IsOneVsOne => Participants.Count == 2 && Participants[0].Players.Length == 1 &&
                              Participants[1].Players.Length == 1;

    public bool StartedBeginCountdown { get; private set; }

    public bool StartedReadyCountdown { get; private set; }

    public Tournament Tournament => m_Tournament;

    private bool CantDoAnything(Mobile mob)
    {
      if (m_EventGame != null)
        return m_EventGame.CantDoAnything(mob);

      return false;
    }

    public static bool IsFreeConsume(Mobile mob)
    {
      if (!(mob is PlayerMobile pm) || pm.DuelContext?.m_EventGame == null)
        return false;

      return pm.DuelContext.m_EventGame.FreeConsume;
    }

    public void DelayBounce(TimeSpan ts, Mobile mob, Container corpse)
    {
      Timer.DelayCall(ts, DelayBounce_Callback, mob, corpse);
    }

    public static bool AllowSpecialMove(Mobile from, string name, SpecialMove move) => (from as PlayerMobile)?.DuelContext?.InstAllowSpecialMove(from, name, move) != false;

    public bool InstAllowSpecialMove(Mobile from, string name, SpecialMove move)
    {
      if (!StartedBeginCountdown)
        return true;

      DuelPlayer pl = Find(from);

      if (pl?.Eliminated != false)
        return true;

      if (CantDoAnything(from))
        return false;

      string title = null;

      if (move is NinjaMove)
        title = "Bushido";
      else if (move is SamuraiMove)
        title = "Ninjitsu";

      if (title == null || name == null || Ruleset.GetOption(title, name))
        return true;

      from.SendMessage("The dueling ruleset prevents you from using this move.");
      return false;
    }

    public bool AllowSpellCast(Mobile from, Spell spell)
    {
      if (!StartedBeginCountdown)
        return true;

      if (Find(from)?.Eliminated != false)
        return true;

      if (CantDoAnything(from))
        return false;

      if (spell is RecallSpell)
        from.SendMessage("You may not cast this spell.");

      string title;
      string option;

      switch (spell)
      {
        case ArcanistSpell _:
          title = "Spellweaving";
          option = spell.Name;
          break;
        case PaladinSpell _:
          title = "Chivalry";
          option = spell.Name;
          break;
        case NecromancerSpell _:
          title = "Necromancy";
          option = spell.Name;
          break;
        case NinjaSpell _:
          title = "Ninjitsu";
          option = spell.Name;
          break;
        case SamuraiSpell _:
          title = "Bushido";
          option = spell.Name;
          break;
        case MagerySpell magerySpell:
          title = magerySpell.Circle switch
          {
            SpellCircle.First => "1st Circle",
            SpellCircle.Second => "2nd Circle",
            SpellCircle.Third => "3rd Circle",
            SpellCircle.Fourth => "4th Circle",
            SpellCircle.Fifth => "5th Circle",
            SpellCircle.Sixth => "6th Circle",
            SpellCircle.Seventh => "7th Circle",
            SpellCircle.Eighth => "8th Circle",
            _ => null
          };

          option = magerySpell.Name;
          break;
        default:
          title = "Other Spell";
          option = spell.Name;
          break;
      }

      if (title == null || option == null || Ruleset.GetOption(title, option))
        return true;

      from.SendMessage("The dueling ruleset prevents you from casting this spell.");
      return false;
    }

    public bool AllowItemEquip(Mobile from, Item item)
    {
      if (!StartedBeginCountdown)
        return true;

      DuelPlayer pl = Find(from);

      if (pl?.Eliminated != false)
        return true;

      if (item is Dagger || CheckItemEquip(from, item))
        return true;

      from.SendMessage("The dueling ruleset prevents you from equipping this item.");
      return false;
    }

    public static bool AllowSpecialAbility(Mobile from, string name, bool message)
    {
      if (!(from is PlayerMobile pm))
        return true;

      DuelContext dc = pm.DuelContext;

      // No DuelContext or InstAllowSpecialAbility
      return dc?.InstAllowSpecialAbility(from, name, message) != false;
    }

    public bool InstAllowSpecialAbility(Mobile from, string name, bool message)
    {
      if (!StartedBeginCountdown)
        return true;

      DuelPlayer pl = Find(from);

      if (pl?.Eliminated != false)
        return true;

      if (CantDoAnything(from))
        return false;

      if (Ruleset.GetOption("Combat Abilities", name))
        return true;

      if (message)
        from.SendMessage("The dueling ruleset prevents you from using this combat ability.");

      return false;
    }

    public bool CheckItemEquip(Mobile from, Item item)
    {
      if (item is Fists)
      {
        if (!Ruleset.GetOption("Weapons", "Wrestling"))
          return false;
      }
      else if (item is BaseArmor armor)
      {
        if (armor.ProtectionLevel > ArmorProtectionLevel.Regular && !Ruleset.GetOption("Armor", "Magical"))
          return false;

        if (!Core.AOS && armor.Resource != armor.DefaultResource && !Ruleset.GetOption("Armor", "Colored"))
          return false;

        if (armor is BaseShield && !Ruleset.GetOption("Armor", "Shields"))
          return false;
      }
      else if (item is BaseWeapon weapon)
      {
        if ((weapon.DamageLevel > WeaponDamageLevel.Regular || weapon.AccuracyLevel > WeaponAccuracyLevel.Regular) &&
            !Ruleset.GetOption("Weapons", "Magical"))
          return false;

        if (!Core.AOS && weapon.Resource != CraftResource.Iron && weapon.Resource != CraftResource.None &&
            !Ruleset.GetOption("Weapons", "Runics"))
          return false;

        if (weapon is BaseRanged && !Ruleset.GetOption("Weapons", "Ranged"))
          return false;

        if (!(weapon is BaseRanged) && !Ruleset.GetOption("Weapons", "Melee"))
          return false;

        if (weapon.PoisonCharges > 0 && weapon.Poison != null && !Ruleset.GetOption("Weapons", "Poisoned"))
          return false;

        if (weapon is BaseWand && !Ruleset.GetOption("Items", "Wands"))
          return false;
      }

      return true;
    }

    public bool AllowSkillUse(Mobile from, SkillName skill)
    {
      if (!StartedBeginCountdown)
        return true;

      DuelPlayer pl = Find(from);

      if (pl?.Eliminated != false)
        return true;

      if (CantDoAnything(from))
        return false;

      int id = (int)skill;

      if (id >= 0 && id < SkillInfo.Table.Length)
        if (Ruleset.GetOption("Skills", SkillInfo.Table[id].Name))
          return true;

      from.SendMessage("The dueling ruleset prevents you from using this skill.");
      return false;
    }

    public bool AllowItemUse(Mobile from, Item item)
    {
      if (!StartedBeginCountdown)
        return true;

      DuelPlayer pl = Find(from);

      if (pl?.Eliminated != false)
        return true;

      if (!(item is BaseRefreshPotion))
        if (CantDoAnything(from))
          return false;

      string title = null, option = null;

      if (item is BasePotion)
      {
        title = "Potions";

        if (item is BaseAgilityPotion)
          option = "Agility";
        else if (item is BaseCurePotion)
          option = "Cure";
        else if (item is BaseHealPotion)
          option = "Heal";
        else if (item is NightSightPotion)
          option = "Nightsight";
        else if (item is BasePoisonPotion)
          option = "Poison";
        else if (item is BaseStrengthPotion)
          option = "Strength";
        else if (item is BaseExplosionPotion)
          option = "Explosion";
        else if (item is BaseRefreshPotion)
          option = "Refresh";
      }
      else if (item is Bandage)
      {
        title = "Items";
        option = "Bandages";
      }
      else if (item is TrappableContainer container)
      {
        if (container.TrapType != TrapType.None)
        {
          title = "Items";
          option = "Trapped Containers";
        }
      }
      else if (item is Bola)
      {
        title = "Items";
        option = "Bolas";
      }
      else if (item is OrangePetals)
      {
        title = "Items";
        option = "Orange Petals";
      }
      else if (item is EtherealMount || item.Layer == Layer.Mount)
      {
        title = "Items";
        option = "Mounts";
      }
      else if (item is LeatherNinjaBelt)
      {
        title = "Items";
        option = "Shurikens";
      }
      else if (item is Fukiya)
      {
        title = "Items";
        option = "Fukiya Darts";
      }
      else if (item is FireHorn)
      {
        title = "Items";
        option = "Fire Horns";
      }
      else if (item is BaseWand)
      {
        title = "Items";
        option = "Wands";
      }

      if (title != null && option != null && StartedBeginCountdown && !Started)
      {
        from.SendMessage("You may not use this item before the duel begins.");
        return false;
      }

      if (item is BasePotion && !(item is BaseExplosionPotion) && !(item is BaseRefreshPotion) && IsSuddenDeath)
      {
        from.SendMessage(0x22, "You may not drink potions in sudden death.");
        return false;
      }

      if (item is Bandage && IsSuddenDeath)
      {
        from.SendMessage(0x22, "You may not use bandages in sudden death.");
        return false;
      }

      if (title == null || option == null || Ruleset.GetOption(title, option))
        return true;

      from.SendMessage("The dueling ruleset prevents you from using this item.");
      return false;
    }

    private void DelayBounce_Callback(Mobile mob, Container corpse)
    {
      RemoveAggressions(mob);
      SendOutside(mob);
      Refresh(mob, corpse);
      Debuff(mob);
      CancelSpell(mob);
      mob.Frozen = false;
    }

    public void OnMapChanged(Mobile mob)
    {
      OnLocationChanged(mob);
    }

    public void OnLocationChanged(Mobile mob)
    {
      if (!Registered || !StartedBeginCountdown || Finished)
        return;

      Arena arena = Arena;

      if (arena == null)
        return;

      if (mob.Map == arena.Facet && arena.Bounds.Contains(mob.Location))
        return;

      DuelPlayer pl = Find(mob);

      if (pl?.Eliminated != false)
        return;

      if (mob.Map == Map.Internal)
        if (mob.LogoutMap == arena.Facet && arena.Bounds.Contains(mob.LogoutLocation))
          mob.LogoutLocation = arena.Outside;

      pl.Eliminated = true;

      mob.LocalOverheadMessage(MessageType.Regular, 0x22, false, "You have forfeited your position in the duel.");
      mob.NonlocalOverheadMessage(MessageType.Regular, 0x22, false,
        $"{mob.Name} has forfeited by leaving the dueling arena.");

      Participant winner = CheckCompletion();

      if (winner != null)
        Finish(winner);
    }

    public void OnDeath(Mobile mob, Container corpse)
    {
      if (!Registered || !Started)
        return;

      DuelPlayer pl = Find(mob);

      if (pl?.Eliminated != false || m_EventGame?.OnDeath(mob, corpse) == false)
        return;

      pl.Eliminated = true;

      if (mob.Poison != null)
        mob.Poison = null;

      Requip(mob, corpse);
      DelayBounce(TimeSpan.FromSeconds(4.0), mob, corpse);

      Participant winner = CheckCompletion();

      if (winner != null)
      {
        Finish(winner);
      }
      else if (!m_Yielding)
      {
        mob.LocalOverheadMessage(MessageType.Regular, 0x22, false, "You have been defeated.");
        mob.NonlocalOverheadMessage(MessageType.Regular, 0x22, false, $"{mob.Name} has been defeated.");
      }
    }

    public bool CheckFull()
    {
      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        if (p.HasOpenSlot)
          return false;
      }

      return true;
    }

    public void Requip(Mobile from, Container cont)
    {
      if (!(cont is Corpse corpse))
        return;

      List<Item> items = new List<Item>(corpse.Items);

      bool didntFit = false;

      Container pack = from.Backpack;

      for (int i = 0; !didntFit && i < items.Count; ++i)
      {
        Item item = items[i];

        if (item.Layer == Layer.Hair || item.Layer == Layer.FacialHair || !item.Movable)
          continue;

        if (pack != null)
          pack.DropItem(item);
        else
          didntFit = true;
      }

      corpse.Carved = true;

      if (corpse.ItemID == 0x2006)
      {
        corpse.ProcessDelta();
        corpse.SendRemovePacket();
        corpse.ItemID = Utility.Random(0xECA, 9); // bone graphic
        corpse.Hue = 0;
        corpse.ProcessDelta();

        Mobile killer = from.FindMostRecentDamager(false);

        if (killer?.Player == true)
          killer.AddToBackpack(new Head(m_Tournament == null ? HeadType.Duel : HeadType.Tournament, from.Name));
      }

      from.PlaySound(0x3E3);

      if (didntFit)
        from.SendLocalizedMessage(1062472); // You gather some of your belongings. The rest remain on the corpse.
      else
        from.SendLocalizedMessage(1062471); // You quickly gather all of your belongings.
    }

    public void Refresh(Mobile mob, Container cont)
    {
      if (!mob.Alive)
      {
        mob.Resurrect();

        if (mob.FindItemOnLayer(Layer.OuterTorso) is DeathRobe robe)
          robe.Delete();

        if (cont is Corpse corpse)
          for (int i = 0; i < corpse.EquipItems.Count; ++i)
          {
            Item item = corpse.EquipItems[i];

            if (item.Movable && item.Layer != Layer.Hair && item.Layer != Layer.FacialHair &&
                item.IsChildOf(mob.Backpack))
              mob.EquipItem(item);
          }
      }

      mob.Hits = mob.HitsMax;
      mob.Stam = mob.StamMax;
      mob.Mana = mob.ManaMax;

      mob.Poison = null;
    }

    public void SendOutside(Mobile mob)
    {
      if (Arena == null)
        return;

      mob.Combatant = null;
      mob.MoveToWorld(Arena.Outside, Arena.Facet);
    }

    public void Finish(Participant winner)
    {
      if (Finished)
        return;

      EndAutoTie();
      StopSDTimers();

      Finished = true;

      for (int i = 0; i < winner.Players.Length; ++i)
      {
        DuelPlayer pl = winner.Players[i];

        if (pl?.Eliminated == false)
          DelayBounce(TimeSpan.FromSeconds(8.0), pl.Mobile, null);
      }

      winner.Broadcast(0x59, null,
        winner.Players.Length == 1 ? "{0} has won the duel." : "{0} and {1} team have won the duel.",
        winner.Players.Length == 1 ? "You have won the duel." : "Your team has won the duel.");

      if (m_Tournament != null && winner.TourneyPart != null)
      {
        m_Match.Winner = winner.TourneyPart;
        winner.TourneyPart.WonMatch(m_Match);
        m_Tournament.HandleWon(Arena, m_Match, winner.TourneyPart);
      }

      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant loser = Participants[i];

        if (loser != winner)
        {
          loser.Broadcast(0x22, null,
            loser.Players.Length == 1 ? "{0} has lost the duel." : "{0} and {1} team have lost the duel.",
            loser.Players.Length == 1 ? "You have lost the duel." : "Your team has lost the duel.");

          if (m_Tournament != null)
            loser.TourneyPart?.LostMatch(m_Match);
        }

        for (int j = 0; j < loser.Players.Length; ++j)
          if (loser.Players[j] != null)
          {
            RemoveAggressions(loser.Players[j].Mobile);
            loser.Players[j].Mobile.Delta(MobileDelta.Noto);
            loser.Players[j].Mobile.CloseGump<BeginGump>();

            if (m_Tournament != null)
              loser.Players[j].Mobile.SendEverything();
          }
      }

      if (IsOneVsOne)
      {
        DuelPlayer dp1 = Participants[0].Players[0];
        DuelPlayer dp2 = Participants[1].Players[0];

        if (dp1 != null && dp2 != null)
        {
          Award(dp1.Mobile, dp2.Mobile, dp1.Participant == winner);
          Award(dp2.Mobile, dp1.Mobile, dp2.Participant == winner);
        }
      }

      m_EventGame?.OnStop();

      Timer.DelayCall(TimeSpan.FromSeconds(9.0), UnregisterRematch);
    }

    public void Award(Mobile us, Mobile them, bool won)
    {
      Ladder ladder = Arena == null ? Ladder.Instance : Arena.AcquireLadder();

      if (ladder == null)
        return;

      LadderEntry ourEntry = ladder.Find(us);
      LadderEntry theirEntry = ladder.Find(them);

      if (ourEntry == null || theirEntry == null)
        return;

      int xpGain = Ladder.GetExperienceGain(ourEntry, theirEntry, won);

      if (xpGain == 0)
        return;

      if (m_Tournament != null)
        xpGain *= xpGain > 0 ? 5 : 2;

      if (won)
        ++ourEntry.Wins;
      else
        ++ourEntry.Losses;

      int oldLevel = Ladder.GetLevel(ourEntry.Experience);

      ourEntry.Experience += xpGain;

      if (ourEntry.Experience < 0)
        ourEntry.Experience = 0;

      ladder.UpdateEntry(ourEntry);

      int newLevel = Ladder.GetLevel(ourEntry.Experience);

      if (newLevel > oldLevel)
        us.SendMessage(0x59, "You have achieved level {0}!", newLevel);
      else if (newLevel < oldLevel)
        us.SendMessage(0x22, "You have lost a level. You are now at {0}.", newLevel);
    }

    public void UnregisterRematch()
    {
      Unregister(true);
    }

    public void Unregister()
    {
      Unregister(false);
    }

    public void Unregister(bool queryRematch)
    {
      DestroyWall();

      if (!Registered)
        return;

      Registered = false;

      Arena?.Evict();

      StopSDTimers();

      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        for (int j = 0; j < p.Players.Length; ++j)
        {
          DuelPlayer pl = p.Players[j];

          if (pl == null)
            continue;

          if (pl.Mobile is PlayerMobile mobile)
            mobile.DuelPlayer = null;

          CloseAllGumps(pl);
        }
      }

      if (queryRematch && m_Tournament == null)
        QueryRematch();
    }

    public void QueryRematch()
    {
      DuelContext dc = new DuelContext(Initiator, Ruleset.Layout, false);

      dc.Ruleset = Ruleset;
      dc.Rematch = true;

      dc.Participants.Clear();

      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant oldPart = Participants[i];
        Participant newPart = new Participant(dc, oldPart.Players.Length);

        for (int j = 0; j < oldPart.Players.Length; ++j)
        {
          DuelPlayer oldPlayer = oldPart.Players[j];

          if (oldPlayer != null)
            newPart.Players[j] = new DuelPlayer(oldPlayer.Mobile, newPart);
        }

        dc.Participants.Add(newPart);
      }

      dc.CloseAllGumps();
      dc.SendReadyUpGump();
    }

    public DuelPlayer Find(Mobile mob)
    {
      if (mob is PlayerMobile pm)
      {
        if (pm.DuelContext == this)
          return pm.DuelPlayer;

        return null;
      }

      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];
        DuelPlayer pl = p.Find(mob);

        if (pl != null)
          return pl;
      }

      return null;
    }

    public bool IsAlly(Mobile m1, Mobile m2)
    {
      DuelPlayer pl1 = Find(m1);
      DuelPlayer pl2 = Find(m2);

      return pl1 != null && pl1.Participant == pl2?.Participant;
    }

    public Participant CheckCompletion()
    {
      Participant winner = null;

      bool hasWinner = false;
      int eliminated = 0;

      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        if (p.Eliminated)
        {
          ++eliminated;

          if (eliminated == Participants.Count - 1)
            hasWinner = true;
        }
        else
        {
          winner = p;
        }
      }

      return hasWinner ? winner ?? Participants[0] : null;
    }

    public void StartCountdown(int count, CountdownCallback cb)
    {
      cb(count);
      m_Countdown = Timer.DelayCall(TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.0), count,
        () => Countdown_Callback(--count, cb));
    }

    public void StopCountdown()
    {
      m_Countdown?.Stop();
      m_Countdown = null;
    }

    private void Countdown_Callback(int count, CountdownCallback cb)
    {
      if (count == 0)
        StopCountdown();

      cb(count);
    }

    public void StopSDTimers()
    {
      m_SDWarnTimer?.Stop();

      m_SDWarnTimer = null;

      m_SDActivateTimer?.Stop();

      m_SDActivateTimer = null;
    }

    public void StartSuddenDeath(TimeSpan timeUntilActive)
    {
      m_SDWarnTimer?.Stop();

      m_SDWarnTimer = Timer.DelayCall(TimeSpan.FromMinutes(timeUntilActive.TotalMinutes * 0.9), WarnSuddenDeath);

      m_SDActivateTimer?.Stop();

      m_SDActivateTimer = Timer.DelayCall(timeUntilActive, ActivateSuddenDeath);
    }

    public void WarnSuddenDeath()
    {
      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        for (int j = 0; j < p.Players.Length; ++j)
        {
          DuelPlayer pl = p.Players[j];

          if (pl?.Eliminated != false)
            continue;

          pl.Mobile.SendSound(0x1E1);
          pl.Mobile.SendMessage(0x22, "Warning! Warning! Warning!");
          pl.Mobile.SendMessage(0x22, "Sudden death will be active soon!");
        }
      }

      m_Tournament?.Alert(Arena, "Sudden death will be active soon!");

      m_SDWarnTimer?.Stop();

      m_SDWarnTimer = null;
    }

    public static bool CheckSuddenDeath(Mobile mob) => mob is PlayerMobile pm && pm.DuelPlayer?.Eliminated == false && pm.DuelContext?.IsSuddenDeath == true;

    public void ActivateSuddenDeath()
    {
      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        for (int j = 0; j < p.Players.Length; ++j)
        {
          DuelPlayer pl = p.Players[j];

          if (pl?.Eliminated != false)
            continue;

          pl.Mobile.SendSound(0x1E1);
          pl.Mobile.SendMessage(0x22, "Warning! Warning! Warning!");
          pl.Mobile.SendMessage(0x22,
            "Sudden death has ACTIVATED. You are now unable to perform any beneficial actions.");
        }
      }

      m_Tournament?.Alert(Arena, "Sudden death has been activated!");

      IsSuddenDeath = true;

      m_SDActivateTimer?.Stop();

      m_SDActivateTimer = null;
    }

    public void BeginAutoTie()
    {
      m_AutoTieTimer?.Stop();

      TimeSpan ts = m_Tournament == null || m_Tournament.TourneyType == TourneyType.Standard
        ? AutoTieDelay
        : TimeSpan.FromMinutes(90.0);

      m_AutoTieTimer = Timer.DelayCall(ts, InvokeAutoTie);
    }

    public void EndAutoTie()
    {
      m_AutoTieTimer?.Stop();

      m_AutoTieTimer = null;
    }

    public void InvokeAutoTie()
    {
      m_AutoTieTimer = null;

      if (!Started || Finished)
        return;

      Tied = true;
      Finished = true;

      StopSDTimers();

      List<TourneyParticipant> remaining = new List<TourneyParticipant>();

      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        if (p.Eliminated)
        {
          p.Broadcast(0x22, null,
            p.Players.Length == 1 ? "{0} has lost the duel." : "{0} and {1} team have lost the duel.",
            p.Players.Length == 1 ? "You have lost the duel." : "Your team has lost the duel.");
        }
        else
        {
          p.Broadcast(0x59, null,
            p.Players.Length == 1
              ? "{0} has tied the duel due to time expiration."
              : "{0} and {1} team have tied the duel due to time expiration.",
            p.Players.Length == 1
              ? "You have tied the duel due to time expiration."
              : "Your team has tied the duel due to time expiration.");

          for (int j = 0; j < p.Players.Length; ++j)
          {
            DuelPlayer pl = p.Players[j];

            if (pl?.Eliminated == false)
              DelayBounce(TimeSpan.FromSeconds(8.0), pl.Mobile, null);
          }

          if (p.TourneyPart != null)
            remaining.Add(p.TourneyPart);
        }

        for (int j = 0; j < p.Players.Length; ++j)
        {
          DuelPlayer pl = p.Players[j];

          if (pl != null)
          {
            pl.Mobile.Delta(MobileDelta.Noto);
            pl.Mobile.SendEverything();
          }
        }
      }

      m_Tournament?.HandleTie(Arena, m_Match, remaining);

      Timer.DelayCall(TimeSpan.FromSeconds(10.0), Unregister);
    }

    public static void Initialize()
    {
      EventSink.Speech += EventSink_Speech;
      EventSink.Login += EventSink_Login;

      CommandSystem.Register("vli", AccessLevel.GameMaster, vli_oc);
    }

    private static void vli_oc(CommandEventArgs e)
    {
      e.Mobile.BeginTarget(-1, false, TargetFlags.None, vli_ot);
    }

    private static void vli_ot(Mobile from, object obj)
    {
      if (obj is PlayerMobile pm)
      {
        Ladder ladder = Ladder.Instance;

        if (ladder == null)
          return;

        LadderEntry entry = ladder.Find(pm);

        if (entry != null)
          from.SendGump(new PropertiesGump(from, entry));
      }
    }

    public static bool CheckCombat(Mobile m) =>
      m.Aggressed.Any(info => info.Defender.Player && DateTime.UtcNow - info.LastCombatTime < CombatDelay) ||
      m.Aggressors.Any(info => info.Attacker.Player && DateTime.UtcNow - info.LastCombatTime < CombatDelay);

    private static void EventSink_Login(Mobile m)
    {
      if (!(m is PlayerMobile pm))
        return;

      DuelContext dc = pm.DuelContext;

      if (dc == null)
        return;

      if (dc.ReadyWait && pm.DuelPlayer.Ready && !dc.Started && !dc.StartedBeginCountdown && !dc.Finished)
      {
        if (dc.m_Tournament == null)
          pm.SendGump(new ReadyGump(pm, dc, dc.ReadyCount));
      }
      else if (dc.ReadyWait && !dc.StartedBeginCountdown && !dc.Started && !dc.Finished)
      {
        if (dc.m_Tournament == null)
          pm.SendGump(new ReadyUpGump(pm, dc));
      }
      else if (dc.Initiator == pm && !dc.ReadyWait && !dc.StartedBeginCountdown && !dc.Started && !dc.Finished)
      {
        pm.SendGump(new DuelContextGump(pm, dc));
      }
    }

    private static void ViewLadder_OnTarget(Mobile from, object obj, Ladder ladder)
    {
      if (obj is PlayerMobile pm)
      {
        LadderEntry entry = ladder.Find(pm);

        if (entry == null)
          return; // sanity

        string text =
          $"{{0}} are ranked {LadderGump.Rank(entry.Index + 1)} at level {Ladder.GetLevel(entry.Experience)}.";

        pm.PrivateOverheadMessage(MessageType.Regular, pm.SpeechHue, true,
          string.Format(text, from == pm ? "You" : "They"), from.NetState);
      }
      else if (obj is Mobile mob)
      {
        if (mob.Body.IsHuman)
          mob.PrivateOverheadMessage(MessageType.Regular, mob.SpeechHue, false,
            "I'm not a duelist, and quite frankly, I resent the implication.", from.NetState);
        else
          mob.PrivateOverheadMessage(MessageType.Regular, 0x3B2, true, "It's probably better than you.",
            from.NetState);
      }
      else
      {
        from.SendMessage("That's not a player.");
      }
    }

    private static void EventSink_Speech(SpeechEventArgs e)
    {
      if (e.Handled)
        return;

      if (!(e.Mobile is PlayerMobile pm))
        return;

      if (Insensitive.Contains(e.Speech, "i wish to duel"))
      {
        if (!pm.CheckAlive())
        {
        }
        else if (pm.Region.IsPartOf<JailRegion>())
        {
        }
        else if (CheckCombat(pm))
        {
          e.Mobile.SendMessage(0x22,
            "You have recently been in combat with another player and must wait before starting a duel.");
        }
        else if (pm.DuelContext != null)
        {
          if (pm.DuelContext.Initiator == pm)
            e.Mobile.SendMessage(0x22, "You have already started a duel.");
          else
            e.Mobile.SendMessage(0x22, "You have already been challenged in a duel.");
        }
        else if (TournamentController.IsActive)
        {
          e.Mobile.SendMessage(0x22, "You may not start a duel while a tournament is active.");
        }
        else
        {
          pm.SendGump(new DuelContextGump(pm, new DuelContext(pm, RulesetLayout.Root)));
          e.Handled = true;
        }
      }
      else if (Insensitive.Equals(e.Speech, "change arena preferences"))
      {
        if (!pm.CheckAlive())
        {
        }
        else
        {
          Preferences prefs = Preferences.Instance;

          if (prefs != null)
          {
            e.Mobile.CloseGump<PreferencesGump>();
            e.Mobile.SendGump(new PreferencesGump(e.Mobile, prefs));
          }
        }
      }
      else if (Insensitive.Equals(e.Speech, "showladder"))
      {
        e.Blocked = true;
        if (!pm.CheckAlive())
        {
        }
        else
        {
          Ladder instance = Ladder.Instance;

          if (instance == null)
          {
            // pm.SendMessage( "Ladder not yet initialized." );
          }
          else
          {
            LadderEntry entry = instance.Find(pm);

            if (entry == null)
              return; // sanity

            string text =
              $"{{0}} {{1}} ranked {LadderGump.Rank(entry.Index + 1)} at level {Ladder.GetLevel(entry.Experience)}.";

            pm.LocalOverheadMessage(MessageType.Regular, pm.SpeechHue, true, string.Format(text, "You", "are"));
            pm.NonlocalOverheadMessage(MessageType.Regular, pm.SpeechHue, true,
              string.Format(text, pm.Name, "is"));

            // pm.PublicOverheadMessage( MessageType.Regular, pm.SpeechHue, true, String.Format( "Level {0} with {1} win{2} and {3} loss{4}.", Ladder.GetLevel( entry.Experience ), entry.Wins, entry.Wins==1?"":"s", entry.Losses, entry.Losses==1?"":"es" ) );
            // pm.PublicOverheadMessage( MessageType.Regular, pm.SpeechHue, true, String.Format( "Level {0} with {1} win{2} and {3} loss{4}.", Ladder.GetLevel( entry.Experience ), entry.Wins, entry.Wins==1?"":"s", entry.Losses, entry.Losses==1?"":"es" ) );
          }
        }
      }
      else if (Insensitive.Equals(e.Speech, "viewladder"))
      {
        e.Blocked = true;

        if (!pm.CheckAlive())
        {
        }
        else
        {
          Ladder instance = Ladder.Instance;

          if (instance == null)
          {
            // pm.SendMessage( "Ladder not yet initialized." );
          }
          else
          {
            pm.SendMessage("Target a player to view their ranking and level.");
            pm.BeginTarget(16, false, TargetFlags.None, ViewLadder_OnTarget, instance);
          }
        }
      }
      else if (Insensitive.Contains(e.Speech, "i yield"))
      {
        if (!pm.CheckAlive())
        {
        }
        else if (pm.DuelContext == null)
        {
        }
        else if (pm.DuelContext.Finished)
        {
          e.Mobile.SendMessage(0x22, "The duel is already finished.");
        }
        else if (!pm.DuelContext.Started)
        {
          DuelContext dc = pm.DuelContext;
          Mobile init = dc.Initiator;

          if (pm.DuelContext.StartedBeginCountdown)
          {
            e.Mobile.SendMessage(0x22, "The duel has not yet started.");
          }
          else
          {
            DuelPlayer pl = pm.DuelContext.Find(pm);

            if (pl == null)
              return;

            Participant p = pl.Participant;

            if (!pm.DuelContext.ReadyWait) // still setting stuff up
            {
              p.Broadcast(0x22, null, "{0} has yielded.", "You have yielded.");

              if (init == pm)
              {
                dc.Unregister();
              }
              else
              {
                p.Nullify(pl);
                pm.DuelPlayer = null;

                NetState ns = init.NetState;

                if (ns != null)
                  foreach (Gump g in ns.Gumps)
                  {
                    if (g is ParticipantGump pg && pg.Participant == p)
                    {
                      init.SendGump(new ParticipantGump(init, dc, p));
                      break;
                    }

                    if (g is DuelContextGump dcg && dcg.Context == dc)
                    {
                      init.SendGump(new DuelContextGump(init, dc));
                      break;
                    }
                  }
              }
            }
            else if (!pm.DuelContext.StartedReadyCountdown) // at ready stage
            {
              p.Broadcast(0x22, null, "{0} has yielded.", "You have yielded.");

              dc.m_Yielding = true;
              dc.RejectReady(pm, null);
              dc.m_Yielding = false;

              if (init == pm)
              {
                dc.Unregister();
              }
              else if (dc.Registered)
              {
                p.Nullify(pl);
                pm.DuelPlayer = null;

                NetState ns = init.NetState;

                if (ns != null)
                {
                  bool send = true;

                  foreach (Gump g in ns.Gumps)
                  {
                    if (g is ParticipantGump pg && pg.Participant == p)
                    {
                      init.SendGump(new ParticipantGump(init, dc, p));
                      send = false;
                      break;
                    }

                    if (g is DuelContextGump dcg && dcg.Context == dc)
                    {
                      init.SendGump(new DuelContextGump(init, dc));
                      send = false;
                      break;
                    }
                  }

                  if (send)
                    init.SendGump(new DuelContextGump(init, dc));
                }
              }
            }
            else
            {
              pm.DuelContext.m_Countdown?.Stop();
              pm.DuelContext.m_Countdown = null;

              pm.DuelContext.StartedReadyCountdown = false;
              p.Broadcast(0x22, null, "{0} has yielded.", "You have yielded.");

              dc.m_Yielding = true;
              dc.RejectReady(pm, null);
              dc.m_Yielding = false;

              if (init == pm)
              {
                dc.Unregister();
              }
              else if (dc.Registered)
              {
                p.Nullify(pl);
                pm.DuelPlayer = null;

                NetState ns = init.NetState;

                if (ns != null)
                {
                  bool send = true;

                  foreach (Gump g in ns.Gumps)
                  {
                    if (g is ParticipantGump pg && pg.Participant == p)
                    {
                      init.SendGump(new ParticipantGump(init, dc, p));
                      send = false;
                      break;
                    }

                    if (g is DuelContextGump dcg && dcg.Context == dc)
                    {
                      init.SendGump(new DuelContextGump(init, dc));
                      send = false;
                      break;
                    }
                  }

                  if (send)
                    init.SendGump(new DuelContextGump(init, dc));
                }
              }
            }
          }
        }
        else
        {
          DuelPlayer pl = pm.DuelContext.Find(pm);

          if (pl != null)
          {
            if (pm.DuelContext.IsOneVsOne)
            {
              e.Mobile.SendMessage(0x22, "You may not yield a 1 on 1 match.");
            }
            else if (pl.Eliminated)
            {
              e.Mobile.SendMessage(0x22, "You have already been eliminated.");
            }
            else
            {
              pm.LocalOverheadMessage(MessageType.Regular, 0x22, false, "You have yielded.");
              pm.NonlocalOverheadMessage(MessageType.Regular, 0x22, false, $"{pm.Name} has yielded.");

              pm.DuelContext.m_Yielding = true;
              pm.Kill();
              pm.DuelContext.m_Yielding = false;

              if (pm.Alive) // invul, ...
              {
                pl.Eliminated = true;

                pm.DuelContext.RemoveAggressions(pm);
                pm.DuelContext.SendOutside(pm);
                pm.DuelContext.Refresh(pm, null);
                Debuff(pm);
                CancelSpell(pm);
                pm.Frozen = false;

                Participant winner = pm.DuelContext.CheckCompletion();

                if (winner != null)
                  pm.DuelContext.Finish(winner);
              }
            }
          }
          else
          {
            e.Mobile.SendMessage(0x22, "BUG: Unable to find duel context.");
          }
        }
      }
    }

    public void CloseAllGumps(DuelPlayer pl)
    {
      pl.Mobile.CloseGump<BeginGump>();
      pl.Mobile.CloseGump<DuelContextGump>();
      pl.Mobile.CloseGump<ParticipantGump>();
      pl.Mobile.CloseGump<PickRulesetGump>();
      pl.Mobile.CloseGump<ReadyGump>();
      pl.Mobile.CloseGump<ReadyUpGump>();
      pl.Mobile.CloseGump<RulesetGump>();
    }

    public void CloseAllGumps()
    {
      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        for (int j = 0; j < p.Players.Length; ++j)
        {
          DuelPlayer pl = p.Players[j];

          if (pl != null)
            CloseAllGumps(pl);
        }
      }
    }

    public void RejectReady(Mobile rejector, string page)
    {
      if (StartedReadyCountdown)
        return; // sanity

      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        for (int j = 0; j < p.Players.Length; ++j)
        {
          DuelPlayer pl = p.Players[j];

          if (pl == null)
            continue;

          pl.Ready = false;

          Mobile mob = pl.Mobile;

          if (page == null) // yield
          {
            if (mob != rejector)
              mob.SendMessage(0x22, "{0} has yielded.", rejector.Name);
          }
          else
          {
            if (mob == rejector)
              mob.SendMessage(0x22, "You have rejected the {0}.", Rematch ? "rematch" : page);
            else
              mob.SendMessage(0x22, "{0} has rejected the {1}.", rejector.Name, Rematch ? "rematch" : page);
          }

          // Close all of them?
          mob.CloseGump<DuelContextGump>();
          mob.CloseGump<ReadyUpGump>();
          mob.CloseGump<ReadyGump>();
        }
      }

      if (Rematch)
        Unregister();
      else if (!m_Yielding)
        Initiator.SendGump(new DuelContextGump(Initiator, this));

      ReadyWait = false;
      ReadyCount = 0;
    }

    public void SendReadyGump()
    {
      SendReadyGump(-1);
    }

    public static void Debuff(Mobile mob)
    {
      mob.RemoveStatMod("[Magic] Str Offset");
      mob.RemoveStatMod("[Magic] Dex Offset");
      mob.RemoveStatMod("[Magic] Int Offset");
      mob.RemoveStatMod("Concussion");
      mob.RemoveStatMod("blood-rose");
      mob.RemoveStatMod("clarity-potion");

      OrangePetals.RemoveContext(mob);

      mob.Paralyzed = false;
      mob.Hidden = false;

      if (!Core.AOS)
      {
        mob.MagicDamageAbsorb = 0;
        mob.MeleeDamageAbsorb = 0;
        ProtectionSpell.Registry.Remove(mob);

        ArchProtectionSpell.RemoveEntry(mob);

        mob.EndAction<DefensiveSpell>();
      }

      TransformationSpellHelper.RemoveContext(mob, true);
      AnimalForm.RemoveContext(mob, true);

      if (DisguiseTimers.IsDisguised(mob))
        DisguiseTimers.StopTimer(mob);

      if (!mob.CanBeginAction<PolymorphSpell>())
      {
        mob.BodyMod = 0;
        mob.HueMod = -1;
        mob.EndAction<PolymorphSpell>();
      }

      BaseArmor.ValidateMobile(mob);
      BaseClothing.ValidateMobile(mob);

      mob.Hits = mob.HitsMax;
      mob.Stam = mob.StamMax;
      mob.Mana = mob.ManaMax;

      mob.Poison = null;
    }

    public static void CancelSpell(Mobile mob)
    {
      if (mob.Spell is Spell spell)
        spell.Disturb(DisturbType.Kill);

      Target.Cancel(mob);
    }

    public void DestroyWall()
    {
      for (int i = 0; i < m_Walls.Count; ++i)
        m_Walls[i].Delete();

      m_Walls.Clear();
    }

    public void CreateWall()
    {
      if (Arena == null)
        return;

      Point3D start = Arena.Points.EdgeWest;
      Point3D wall = Arena.Wall;

      int dx = start.X - wall.X;
      int dy = start.Y - wall.Y;
      int rx = dx - dy;
      int ry = dx + dy;

      bool eastToWest;

      if (rx >= 0 && ry >= 0)
        eastToWest = false;
      else if (rx >= 0)
        eastToWest = true;
      else if (ry >= 0)
        eastToWest = true;
      else
        eastToWest = false;

      Effects.PlaySound(wall, Arena.Facet, 0x1F6);

      for (int i = -1; i <= 1; ++i)
      {
        Point3D loc = new Point3D(eastToWest ? wall.X + i : wall.X, eastToWest ? wall.Y : wall.Y + i, wall.Z);

        InternalWall created = new InternalWall();

        created.Appear(loc, Arena.Facet);

        m_Walls.Add(created);
      }
    }

    public void BuildParties()
    {
      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        if (p.Players.Length > 1)
        {
          List<Mobile> players = new List<Mobile>();

          for (int j = 0; j < p.Players.Length; ++j)
          {
            DuelPlayer dp = p.Players[j];

            if (dp == null)
              continue;

            players.Add(dp.Mobile);
          }

          if (players.Count > 1)
            for (int leaderIndex = 0; leaderIndex + 1 < players.Count; leaderIndex += Party.Capacity)
            {
              Mobile leader = players[leaderIndex];
              Party party = Party.Get(leader);

              if (party == null)
              {
                leader.Party = party = new Party(leader);
              }
              else if (party.Leader != leader)
              {
                party.SendPublicMessage(leader, "I leave this party to fight in a duel.");
                party.Remove(leader);
                leader.Party = party = new Party(leader);
              }

              for (int j = leaderIndex + 1; j < players.Count && j < leaderIndex + Party.Capacity; ++j)
              {
                Mobile player = players[j];
                Party existing = Party.Get(player);

                if (existing == party)
                  continue;

                if (party.Members.Count + party.Candidates.Count >= Party.Capacity)
                {
                  player.SendMessage(
                    "You could not be added to the team party because it is at full capacity.");
                  leader.SendMessage(
                    "{0} could not be added to the team party because it is at full capacity.");
                }
                else
                {
                  if (existing != null)
                  {
                    existing.SendPublicMessage(player, "I leave this party to fight in a duel.");
                    existing.Remove(player);
                  }

                  party.OnAccept(player, true);
                }
              }
            }
        }
      }
    }

    public void ClearIllegalItems()
    {
      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        for (int j = 0; j < p.Players.Length; ++j)
        {
          DuelPlayer pl = p.Players[j];

          if (pl == null)
            continue;

          ClearIllegalItems(pl.Mobile);
        }
      }
    }

    public void ClearIllegalItems(Mobile mob)
    {
      if (mob.StunReady && !AllowSpecialAbility(mob, "Stun", false))
        mob.StunReady = false;

      if (mob.DisarmReady && !AllowSpecialAbility(mob, "Disarm", false))
        mob.DisarmReady = false;

      Container pack = mob.Backpack;

      if (pack == null)
        return;

      for (int i = mob.Items.Count - 1; i >= 0; --i)
      {
        if (i >= mob.Items.Count)
          continue; // sanity

        Item item = mob.Items[i];

        if (!CheckItemEquip(mob, item))
        {
          pack.DropItem(item);

          if (item is BaseWeapon)
            mob.SendLocalizedMessage(1062001,
              item.Name ?? $"#{item.LabelNumber}"); // You can no longer wield your ~1_WEAPON~
          else if (item is BaseArmor && !(item is BaseShield))
            mob.SendLocalizedMessage(1062002,
              item.Name ?? $"#{item.LabelNumber}"); // You can no longer wear your ~1_ARMOR~
          else
            mob.SendLocalizedMessage(1062003,
              item.Name ?? $"#{item.LabelNumber}"); // You can no longer equip your ~1_SHIELD~
        }
      }

      Item inHand = mob.Holding;

      if (inHand != null && !CheckItemEquip(mob, inHand))
      {
        mob.Holding = null;

        BounceInfo bi = inHand.GetBounce();

        if (bi.Parent == mob)
          pack.DropItem(inHand);
        else
          inHand.Bounce(mob);

        inHand.ClearBounce();
      }
    }

    private void MessageRuleset(Mobile mob)
    {
      if (Ruleset == null) return;

      Ruleset ruleset = Ruleset;
      Ruleset basedef = ruleset.Base;

      mob.SendMessage("Ruleset: {0}", basedef.Title);

      BitArray defs;

      if (ruleset.Flavors.Count > 0)
      {
        defs = new BitArray(basedef.Options);

        for (int i = 0; i < ruleset.Flavors.Count; ++i)
        {
          defs.Or(ruleset.Flavors[i].Options);

          mob.SendMessage(" + {0}", ruleset.Flavors[i].Title);
        }
      }
      else
      {
        defs = basedef.Options;
      }

      int changes = 0;

      BitArray opts = ruleset.Options;

      for (int i = 0; i < opts.Length; ++i)
        if (defs[i] != opts[i])
        {
          string name = ruleset.Layout.FindByIndex(i);

          if (name != null) // sanity
          {
            ++changes;

            if (changes == 1) mob.SendMessage("Modifications:");

            mob.SendMessage("{0}: {1}", name, opts[i] ? "enabled" : "disabled");
          }
        }
    }

    public void SendBeginGump(int count)
    {
      if (!Registered || Finished)
        return;

      if (count == 10)
      {
        CreateWall();
        BuildParties();
        ClearIllegalItems();
      }
      else if (count == 0)
      {
        DestroyWall();
      }

      StartedBeginCountdown = true;

      if (count == 0)
      {
        Started = true;
        BeginAutoTie();
      }

      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        for (int j = 0; j < p.Players.Length; ++j)
        {
          DuelPlayer pl = p.Players[j];

          if (pl == null)
            continue;

          Mobile mob = pl.Mobile;

          if (count > 0)
          {
            if (count == 10)
            {
              mob.CloseGump<ReadyGump>();
              mob.CloseGump<ReadyUpGump>();
              mob.CloseGump<BeginGump>();
              mob.SendGump(new BeginGump(count));
            }

            mob.Frozen = true;
          }
          else
          {
            mob.CloseGump<BeginGump>();
            mob.Frozen = false;
          }
        }
      }
    }

    public void RemoveAggressions(Mobile mob)
    {
      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        for (int j = 0; j < p.Players.Length; ++j)
        {
          DuelPlayer dp = p.Players[j];

          if (dp == null || dp.Mobile == mob)
            continue;

          mob.RemoveAggressed(dp.Mobile);
          mob.RemoveAggressor(dp.Mobile);
          dp.Mobile.RemoveAggressed(mob);
          dp.Mobile.RemoveAggressor(mob);
        }
      }
    }

    public void SendReadyUpGump()
    {
      if (!Registered)
        return;

      ReadyWait = true;
      ReadyCount = -1;

      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        for (int j = 0; j < p.Players.Length; ++j)
        {
          DuelPlayer pl = p.Players[j];

          Mobile mob = pl?.Mobile;

          if (mob != null && m_Tournament == null)
          {
            mob.CloseGump<ReadyUpGump>();
            mob.SendGump(new ReadyUpGump(mob, this));
          }
        }
      }
    }

    public string ValidateStart()
    {
      if (m_Tournament == null && TournamentController.IsActive)
        return "a tournament is active";

      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        for (int j = 0; j < p.Players.Length; ++j)
        {
          DuelPlayer dp = p.Players[j];

          if (dp == null)
            return "a slot is empty";

          if (dp.Mobile.Region.IsPartOf<JailRegion>())
            return $"{dp.Mobile.Name} is in jail";

          if (Sigil.ExistsOn(dp.Mobile))
            return $"{dp.Mobile.Name} is holding a sigil";

          if (!dp.Mobile.Alive)
          {
            if (m_Tournament == null)
              return $"{dp.Mobile.Name} is dead";
            dp.Mobile.Resurrect();
          }

          if (m_Tournament == null && CheckCombat(dp.Mobile))
            return $"{dp.Mobile.Name} is in combat";

          if (dp.Mobile.Mounted)
          {
            IMount mount = dp.Mobile.Mount;

            if (m_Tournament != null && mount != null)
              mount.Rider = null;
            else
              return $"{dp.Mobile.Name} is mounted";
          }
        }
      }

      return null;
    }

    public void SendReadyGump(int count)
    {
      if (!Registered)
        return;

      if (count != -1)
        StartedReadyCountdown = true;

      ReadyCount = count;

      if (count == 0)
      {
        string error = ValidateStart();

        if (error != null)
        {
          for (int i = 0; i < Participants.Count; ++i)
          {
            Participant p = Participants[i];

            for (int j = 0; j < p.Players.Length; ++j)
            {
              DuelPlayer dp = p.Players[j];

              dp?.Mobile.SendMessage("The duel could not be started because {0}.", error);
            }
          }

          StartCountdown(10, SendReadyGump);

          return;
        }

        ReadyWait = false;

        List<Mobile> players = new List<Mobile>();

        for (int i = 0; i < Participants.Count; ++i)
        {
          Participant p = Participants[i];

          for (int j = 0; j < p.Players.Length; ++j)
          {
            DuelPlayer dp = p.Players[j];

            if (dp != null)
              players.Add(dp.Mobile);
          }
        }

        Arena arena = m_OverrideArena ?? Arena.FindArena(players);

        if (arena == null)
        {
          for (int i = 0; i < Participants.Count; ++i)
          {
            Participant p = Participants[i];

            for (int j = 0; j < p.Players.Length; ++j)
            {
              DuelPlayer dp = p.Players[j];

              dp?.Mobile.SendMessage(
                "The duel could not be started because there are no arenas. If you want to stop waiting for a free arena, yield the duel.");
            }
          }

          StartCountdown(10, SendReadyGump);
          return;
        }

        if (!arena.IsOccupied)
        {
          Arena = arena;

          if (Initiator.Map == Map.Internal)
          {
            m_GatePoint = Initiator.LogoutLocation;
            m_GateFacet = Initiator.LogoutMap;
          }
          else
          {
            m_GatePoint = Initiator.Location;
            m_GateFacet = Initiator.Map;
          }

          if (!(arena.Teleporter is ExitTeleporter tp))
          {
            arena.Teleporter = tp = new ExitTeleporter();
            tp.MoveToWorld(arena.GateOut == Point3D.Zero ? arena.Outside : arena.GateOut, arena.Facet);
          }

          ArenaMoongate mg = new ArenaMoongate(arena.GateIn == Point3D.Zero ? arena.Outside : arena.GateIn,
            arena.Facet, tp);

          StartedBeginCountdown = true;

          for (int i = 0; i < Participants.Count; ++i)
          {
            Participant p = Participants[i];

            for (int j = 0; j < p.Players.Length; ++j)
            {
              DuelPlayer pl = p.Players[j];

              if (pl == null)
                continue;

              tp.Register(pl.Mobile);

              pl.Mobile.Frozen = false; // reset timer just in case
              pl.Mobile.Frozen = true;

              Debuff(pl.Mobile);
              CancelSpell(pl.Mobile);

              pl.Mobile.Delta(MobileDelta.Noto);
            }

            arena.MoveInside(p.Players, i);
          }

          m_EventGame?.OnStart();

          StartCountdown(10, SendBeginGump);

          mg.Appear(m_GatePoint, m_GateFacet);
        }
        else
        {
          for (int i = 0; i < Participants.Count; ++i)
          {
            Participant p = Participants[i];

            for (int j = 0; j < p.Players.Length; ++j)
            {
              DuelPlayer dp = p.Players[j];

              dp?.Mobile.SendMessage(
                "The duel could not be started because all arenas are full. If you want to stop waiting for a free arena, yield the duel.");
            }
          }

          StartCountdown(10, SendReadyGump);
        }

        return;
      }

      ReadyWait = true;

      bool isAllReady = true;

      for (int i = 0; i < Participants.Count; ++i)
      {
        Participant p = Participants[i];

        for (int j = 0; j < p.Players.Length; ++j)
        {
          DuelPlayer pl = p.Players[j];

          if (pl == null)
            continue;

          Mobile mob = pl.Mobile;

          if (pl.Ready)
          {
            if (m_Tournament == null)
            {
              mob.CloseGump<ReadyGump>();
              mob.SendGump(new ReadyGump(mob, this, count));
            }
          }
          else
          {
            isAllReady = false;
          }
        }
      }

      if (count == -1 && isAllReady)
        StartCountdown(3, SendReadyGump);
    }

    private class InternalWall : Item
    {
      public InternalWall() : base(0x80) => Movable = false;

      public InternalWall(Serial serial) : base(serial)
      {
      }

      public void Appear(Point3D loc, Map map)
      {
        MoveToWorld(loc, map);

        Effects.SendLocationParticles(this, 0x376A, 9, 10, 5025);
      }

      public override void Serialize(IGenericWriter writer)
      {
        base.Serialize(writer);

        writer.Write(0);
      }

      public override void Deserialize(IGenericReader reader)
      {
        base.Deserialize(reader);

        int version = reader.ReadInt();

        Delete();
      }
    }

    private class ReturnEntry
    {
      private DateTime m_Expire;

      public ReturnEntry(Mobile mob)
      {
        Mobile = mob;

        Update();
      }

      public ReturnEntry(Mobile mob, Point3D loc, Map facet)
      {
        Mobile = mob;
        Location = loc;
        Facet = facet;
        m_Expire = DateTime.UtcNow + TimeSpan.FromMinutes(30.0);
      }

      public Mobile Mobile { get; }

      public Point3D Location { get; private set; }

      public Map Facet { get; private set; }

      public bool Expired => DateTime.UtcNow >= m_Expire;

      public void Return()
      {
        if (Facet == Map.Internal || Facet == null)
          return;

        if (Mobile.Map == Map.Internal)
        {
          Mobile.LogoutLocation = Location;
          Mobile.LogoutMap = Facet;
        }
        else
        {
          Mobile.Location = Location;
          Mobile.Map = Facet;
        }
      }

      public void Update()
      {
        m_Expire = DateTime.UtcNow + TimeSpan.FromMinutes(30.0);

        if (Mobile.Map == Map.Internal)
        {
          Facet = Mobile.LogoutMap;
          Location = Mobile.LogoutLocation;
        }
        else
        {
          Facet = Mobile.Map;
          Location = Mobile.Location;
        }
      }
    }

    private class ExitTeleporter : Item
    {
      private List<ReturnEntry> m_Entries;

      public ExitTeleporter() : base(0x1822)
      {
        m_Entries = new List<ReturnEntry>();

        Hue = 0x482;
        Movable = false;
      }

      public ExitTeleporter(Serial serial) : base(serial)
      {
      }

      public override string DefaultName => "return teleporter";

      public void Register(Mobile mob)
      {
        ReturnEntry entry = Find(mob);

        if (entry != null)
        {
          entry.Update();
          return;
        }

        m_Entries.Add(new ReturnEntry(mob));
      }

      private ReturnEntry Find(Mobile mob)
      {
        for (int i = 0; i < m_Entries.Count; ++i)
        {
          ReturnEntry entry = m_Entries[i];

          if (entry.Mobile == mob)
            return entry;
          if (entry.Expired)
            m_Entries.RemoveAt(i--);
        }

        return null;
      }

      public override bool OnMoveOver(Mobile m)
      {
        if (!base.OnMoveOver(m))
          return false;

        ReturnEntry entry = Find(m);

        if (entry != null)
        {
          entry.Return();

          Effects.PlaySound(GetWorldLocation(), Map, 0x1FE);
          Effects.PlaySound(m.Location, m.Map, 0x1FE);

          m_Entries.Remove(entry);

          return false;
        }

        m.SendLocalizedMessage(1049383); // The teleporter doesn't seem to work for you.
        return true;
      }

      public override void Serialize(IGenericWriter writer)
      {
        base.Serialize(writer);

        writer.Write(0);

        writer.WriteEncodedInt(m_Entries.Count);

        for (int i = 0; i < m_Entries.Count; ++i)
        {
          ReturnEntry entry = m_Entries[i];

          writer.Write(entry.Mobile);
          writer.Write(entry.Location);
          writer.Write(entry.Facet);

          if (entry.Expired)
            m_Entries.RemoveAt(i--);
        }
      }

      public override void Deserialize(IGenericReader reader)
      {
        base.Deserialize(reader);

        int version = reader.ReadInt();

        switch (version)
        {
          case 0:
            {
              int count = reader.ReadEncodedInt();

              m_Entries = new List<ReturnEntry>(count);

              for (int i = 0; i < count; ++i)
              {
                Mobile mob = reader.ReadMobile();
                Point3D loc = reader.ReadPoint3D();
                Map map = reader.ReadMap();

                m_Entries.Add(new ReturnEntry(mob, loc, map));
              }

              break;
            }
        }
      }
    }

    private class ArenaMoongate : ConfirmationMoongate
    {
      private readonly ExitTeleporter m_Teleporter;

      public ArenaMoongate(Point3D target, Map map, ExitTeleporter tp) : base(target, map)
      {
        m_Teleporter = tp;

        ItemID = 0x1FD4;
        Dispellable = false;

        GumpWidth = 300;
        GumpHeight = 150;
        MessageColor = 0xFFC000;
        MessageString = "Are you sure you wish to spectate this duel?";
        TitleColor = 0x7800;
        TitleNumber = 1062051; // Gate Warning

        Timer.DelayCall(TimeSpan.FromSeconds(10.0), Delete);
      }

      public ArenaMoongate(Serial serial) : base(serial)
      {
      }

      public override string DefaultName => "spectator moongate";

      public override void CheckGate(Mobile m, int range)
      {
        if (CheckCombat(m))
          m.SendMessage(0x22,
            "You have recently been in combat with another player and cannot use this moongate.");
        else
          base.CheckGate(m, range);
      }

      public override void UseGate(Mobile m)
      {
        if (CheckCombat(m))
        {
          m.SendMessage(0x22,
            "You have recently been in combat with another player and cannot use this moongate.");
        }
        else
        {
          if (m_Teleporter?.Deleted == false)
            m_Teleporter.Register(m);

          base.UseGate(m);
        }
      }

      public void Appear(Point3D loc, Map map)
      {
        Effects.PlaySound(loc, map, 0x20E);
        MoveToWorld(loc, map);
      }

      public override void Serialize(IGenericWriter writer)
      {
        base.Serialize(writer);

        writer.Write(0);
      }

      public override void Deserialize(IGenericReader reader)
      {
        base.Deserialize(reader);

        int version = reader.ReadInt();

        Delete();
      }
    }
  }
}