using Server.Mobiles;

namespace Server.Engines.Quests.Doom
{
	public class AcceptConversation : QuestConversation
	{
		public override object Message => 1050027;

		public AcceptConversation()
		{
		}

		public override void OnRead()
		{
			System.AddObjective( new CollectBonesObjective() );
		}
	}

	public class VanquishDaemonConversation : QuestConversation
	{
		public override object Message => 1050021;

		public VanquishDaemonConversation()
		{
		}

		public override void OnRead()
		{
			Victoria victoria = ((TheSummoningQuest)System).Victoria;

			if ( victoria == null )
			{
				System.From.SendMessage( "Internal error: unable to find Victoria. Quest unable to continue." );
				System.Cancel();
			}
			else
			{
				SummoningAltar altar = victoria.Altar;

				if ( altar == null )
				{
					System.From.SendMessage( "Internal error: unable to find summoning altar. Quest unable to continue." );
					System.Cancel();
				}
				else if ( altar.Daemon == null || !altar.Daemon.Alive )
				{
					BoneDemon daemon = new BoneDemon();

					daemon.MoveToWorld( altar.Location, altar.Map );
					altar.Daemon = daemon;

					System.AddObjective( new VanquishDaemonObjective( daemon ) );
				}
				else
				{
					victoria.SayTo( System.From, "The devourer has already been summoned." );

					((TheSummoningQuest)System).WaitForSummon = true;
				}
			}
		}
	}
}