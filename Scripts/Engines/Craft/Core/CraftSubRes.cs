using System;

namespace Server.Engines.Craft
{
	public class CraftSubRes
	{
		private Type m_Type;
		private double m_ReqSkill;
		private string m_NameString;
		private int m_NameNumber;
		private int m_GenericNameNumber;
		private object m_Message;

		public CraftSubRes( Type type, TextDefinition name, double reqSkill, object message ) : this( type, name, reqSkill, 0, message )
		{
		}

		public CraftSubRes( Type type, TextDefinition name, double reqSkill, int genericNameNumber, object message )
		{
			m_Type = type;
			m_NameNumber = name;
			m_NameString = name;
			m_ReqSkill = reqSkill;
			m_GenericNameNumber = genericNameNumber;
			m_Message = message;
		}

		public Type ItemType => m_Type;

		public string NameString => m_NameString;

		public int NameNumber => m_NameNumber;

		public int GenericNameNumber => m_GenericNameNumber;

		public object Message => m_Message;

		public double RequiredSkill => m_ReqSkill;
	}
}