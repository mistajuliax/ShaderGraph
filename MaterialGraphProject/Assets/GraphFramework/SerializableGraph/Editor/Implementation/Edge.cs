using System;

namespace UnityEngine.Graphing
{
    [Serializable]
    public class Edge : IEdge
    {
        [SerializeField]
        private SlotReference m_OutputSlot;
        [SerializeField]
        private SlotReference m_InputSlot;

        public Edge()
        {}

        public Edge(SlotReference outputSlot, SlotReference inputSlot)
        {
            m_OutputSlot = outputSlot;
            m_InputSlot = inputSlot;
        }

        public SlotReference outputSlot
        {
            get { return m_OutputSlot; }
        }

        public SlotReference inputSlot
        {
            get { return m_InputSlot; }
        }

        protected bool Equals(Edge other)
        {
            return Equals(m_OutputSlot, other.m_OutputSlot) && Equals(m_InputSlot, other.m_InputSlot);
        }

        public bool Equals(IEdge other)
        {
            return Equals(other as object);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((Edge)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((m_OutputSlot != null ? m_OutputSlot.GetHashCode() : 0) * 397) ^ (m_InputSlot != null ? m_InputSlot.GetHashCode() : 0);
            }
        }
    }
}