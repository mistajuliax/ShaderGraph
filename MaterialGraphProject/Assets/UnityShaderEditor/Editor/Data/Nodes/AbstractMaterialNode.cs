using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine.Graphing;

namespace UnityEngine.MaterialGraph
{
    public static class GuidEncoder
    {
        public static string Encode(Guid guid)
        {
            string enc = Convert.ToBase64String(guid.ToByteArray());
            return String.Format("{0:X}", enc.GetHashCode());
        }
    }

    [Serializable]
    public abstract class AbstractMaterialNode : SerializableNode, IGenerateProperties
    {
        public enum OutputPrecision
        {
            @fixed,
            @half,
            @float
        }

        [NonSerialized]
        private bool m_HasError;

        public OutputPrecision precision
        {
            get { return m_OutputPrecision; }
            set { m_OutputPrecision = value; }
        }

        //[SerializeField]
        private OutputPrecision m_OutputPrecision = OutputPrecision.@float;

        [SerializeField]
        bool m_PreviewExpanded = true;

        public bool previewExpanded
        {
            get { return m_PreviewExpanded; }
            set
            {
                if (previewExpanded == value)
                    return;
                m_PreviewExpanded = value;
                if (onModified != null)
                    onModified(this, ModificationScope.Node);
            }
        }

        // Nodes that want to have a preview area can override this and return true
        public virtual bool hasPreview
        {
            get { return false; }
        }

        public virtual PreviewMode previewMode
        {
            get { return PreviewMode.Preview2D; }
        }

        public virtual bool allowedInSubGraph
        {
            get { return true; }
        }

        public virtual bool allowedInRemapGraph
        {
            get { return true; }
        }

        public override bool hasError
        {
            get { return m_HasError; }
            protected set { m_HasError = value; }
        }

        protected AbstractMaterialNode()
        {
            version = 0;
        }

        public virtual void CollectShaderProperties(PropertyCollector properties, GenerationMode generationMode)
        {
            if (!generationMode.IsPreview())
                return;

            foreach (var inputSlot in GetInputSlots<MaterialSlot>())
            {
                var edges = owner.GetEdges(inputSlot.slotReference);
                if (edges.Any())
                    continue;

                inputSlot.AddDefaultProperty(properties, generationMode);
            }
        }

        public string GetSlotValue(int inputSlotId, GenerationMode generationMode)
        {
            var inputSlot = FindSlot<MaterialSlot>(inputSlotId);
            if (inputSlot == null)
                return string.Empty;

            var edges = owner.GetEdges(inputSlot.slotReference).ToArray();

            if (edges.Any())
            {
                var fromSocketRef = edges[0].outputSlot;
                var fromNode = owner.GetNodeFromGuid<AbstractMaterialNode>(fromSocketRef.nodeGuid);
                if (fromNode == null)
                    return string.Empty;

                var slot = fromNode.FindOutputSlot<MaterialSlot>(fromSocketRef.slotId);
                if (slot == null)
                    return string.Empty;

                return ShaderGenerator.AdaptNodeOutput(fromNode, slot.id, inputSlot.concreteValueType);
            }

            return inputSlot.GetDefaultValue(generationMode);
        }

        private ConcreteSlotValueType FindCommonChannelType(ConcreteSlotValueType from, ConcreteSlotValueType to)
        {
            if (ImplicitConversionExists(from, to))
                return to;

            return ConcreteSlotValueType.Error;
        }

        private static ConcreteSlotValueType ToConcreteType(SlotValueType svt)
        {
            switch (svt)
            {
                case SlotValueType.Vector1:
                    return ConcreteSlotValueType.Vector1;
                case SlotValueType.Vector2:
                    return ConcreteSlotValueType.Vector2;
                case SlotValueType.Vector3:
                    return ConcreteSlotValueType.Vector3;
                case SlotValueType.Vector4:
                    return ConcreteSlotValueType.Vector4;
                case SlotValueType.Texture2D:
                    return ConcreteSlotValueType.Texture2D;
                case SlotValueType.Matrix2:
                    return ConcreteSlotValueType.Matrix2;
                case SlotValueType.Matrix3:
                    return ConcreteSlotValueType.Matrix3;
                case SlotValueType.Matrix4:
                    return ConcreteSlotValueType.Matrix4;
                case SlotValueType.SamplerState:
                    return ConcreteSlotValueType.SamplerState;
            }
            return ConcreteSlotValueType.Error;
        }

        private static bool ImplicitConversionExists(ConcreteSlotValueType from, ConcreteSlotValueType to)
        {
            if (from == to)
                return true;

            var fromCount = SlotValueHelper.GetChannelCount(from);
            var toCount = SlotValueHelper.GetChannelCount(to);


            // can convert from v1 vectors :)
            if (from == ConcreteSlotValueType.Vector1 && toCount > 0)
                return true;

            if (toCount == 0)
                return false;

            if (toCount <= fromCount)
                return true;

            return false;
        }

        private ConcreteSlotValueType ConvertDynamicInputTypeToConcrete(IEnumerable<ConcreteSlotValueType> inputTypes)
        {
            var concreteSlotValueTypes = inputTypes as IList<ConcreteSlotValueType> ?? inputTypes.ToList();
            if (concreteSlotValueTypes.Any(x => x == ConcreteSlotValueType.Error))
                return ConcreteSlotValueType.Error;

            var inputTypesDistinct = concreteSlotValueTypes.Distinct().ToList();
            switch (inputTypesDistinct.Count)
            {
                case 0:
                    return ConcreteSlotValueType.Vector1;
                case 1:
                    return inputTypesDistinct.FirstOrDefault();
                default:
                    // find the 'minumum' channel width excluding 1 as it can promote
                    inputTypesDistinct.RemoveAll(x => x == ConcreteSlotValueType.Vector1);
                    var ordered = inputTypesDistinct.OrderByDescending(x => x);
                    if (ordered.Any())
                        return ordered.FirstOrDefault();
                    break;
            }
            return ConcreteSlotValueType.Error;
        }

        public override void ValidateNode()
        {
            var isInError = false;

            // all children nodes needs to be updated first
            // so do that here
            foreach (var inputSlot in GetInputSlots<MaterialSlot>())
            {
                var edges = owner.GetEdges(inputSlot.slotReference);
                foreach (var edge in edges)
                {
                    var fromSocketRef = edge.outputSlot;
                    var outputNode = owner.GetNodeFromGuid(fromSocketRef.nodeGuid);
                    if (outputNode == null)
                        continue;

                    outputNode.ValidateNode();
                    if (outputNode.hasError)
                        isInError = true;
                }
            }

            var dynamicInputSlotsToCompare = DictionaryPool<MaterialSlot, ConcreteSlotValueType>.Get();
            var skippedDynamicSlots = ListPool<MaterialSlot>.Get();

            // iterate the input slots
            foreach (var inputSlot in GetInputSlots<MaterialSlot>())
            {
                var inputType = inputSlot.valueType;
                // if there is a connection
                var edges = owner.GetEdges(inputSlot.slotReference).ToList();
                if (!edges.Any())
                {
                    if (inputType != SlotValueType.Dynamic)
                        inputSlot.concreteValueType = ToConcreteType(inputType);
                    else
                        skippedDynamicSlots.Add(inputSlot);
                    continue;
                }

                // get the output details
                var outputSlotRef = edges[0].outputSlot;
                var outputNode = owner.GetNodeFromGuid(outputSlotRef.nodeGuid);
                if (outputNode == null)
                    continue;

                var outputSlot = outputNode.FindOutputSlot<MaterialSlot>(outputSlotRef.slotId);
                if (outputSlot == null)
                    continue;

                var outputConcreteType = outputSlot.concreteValueType;

                // if we have a standard connection... just check the types work!
                if (inputType != SlotValueType.Dynamic)
                {
                    var inputConcreteType = ToConcreteType(inputType);
                    inputSlot.concreteValueType = FindCommonChannelType(outputConcreteType, inputConcreteType);
                    continue;
                }

                // dynamic input... depends on output from other node.
                // we need to compare ALL dynamic inputs to make sure they
                // are compatable.
                dynamicInputSlotsToCompare.Add(inputSlot, outputConcreteType);
            }

            // we can now figure out the dynamic slotType
            // from here set all the
            var dynamicType = ConvertDynamicInputTypeToConcrete(dynamicInputSlotsToCompare.Values);
            foreach (var dynamicKvP in dynamicInputSlotsToCompare)
                dynamicKvP.Key.concreteValueType = dynamicType;
            foreach (var skippedSlot in skippedDynamicSlots)
                skippedSlot.concreteValueType = dynamicType;

            var inputError = GetInputSlots<MaterialSlot>().Any(x => x.concreteValueType == ConcreteSlotValueType.Error);

            // configure the output slots now
            // their slotType will either be the default output slotType
            // or the above dynanic slotType for dynamic nodes
            // or error if there is an input error
            foreach (var outputSlot in GetOutputSlots<MaterialSlot>())
            {
                if (inputError)
                {
                    outputSlot.concreteValueType = ConcreteSlotValueType.Error;
                    continue;
                }

                if (outputSlot.valueType == SlotValueType.Dynamic)
                {
                    outputSlot.concreteValueType = dynamicType;
                    continue;
                }
                outputSlot.concreteValueType = ToConcreteType(outputSlot.valueType);
            }

            isInError |= inputError;
            isInError |= GetOutputSlots<MaterialSlot>().Any(x => x.concreteValueType == ConcreteSlotValueType.Error);
            isInError |= CalculateNodeHasError();
            hasError = isInError;

            if (!hasError)
            {
                ++version;
            }

            ListPool<MaterialSlot>.Release(skippedDynamicSlots);
            DictionaryPool<MaterialSlot, ConcreteSlotValueType>.Release(dynamicInputSlotsToCompare);

        }

        public int version { get; private set; }

        //True if error
        protected virtual bool CalculateNodeHasError()
        {
            return false;
        }

        [Obsolete("Call new override", false)]
        public static string ConvertConcreteSlotValueTypeToString(ConcreteSlotValueType slotValue)
        {
            switch (slotValue)
            {
                case ConcreteSlotValueType.Vector1:
                    return string.Empty;
                case ConcreteSlotValueType.Vector2:
                    return "2";
                case ConcreteSlotValueType.Vector3:
                    return "3";
                case ConcreteSlotValueType.Vector4:
                    return "4";
                case ConcreteSlotValueType.Texture2D:
                    return "Texture2D";
                case ConcreteSlotValueType.Matrix2:
                    return "2x2";
                case ConcreteSlotValueType.Matrix3:
                    return "3x3";
                case ConcreteSlotValueType.Matrix4:
                    return "4x4";
                case ConcreteSlotValueType.SamplerState:
                    return "SamplerState";
                default:
                    return "Error";
            }
        }

        public static string ConvertConcreteSlotValueTypeToString(OutputPrecision p, ConcreteSlotValueType slotValue)
        {
            switch (slotValue)
            {
                case ConcreteSlotValueType.Vector1:
                    return p.ToString();
                case ConcreteSlotValueType.Vector2:
                    return p + "2";
                case ConcreteSlotValueType.Vector3:
                    return p + "3";
                case ConcreteSlotValueType.Vector4:
                    return p + "4";
                case ConcreteSlotValueType.Texture2D:
                    return "Texture2D";
                case ConcreteSlotValueType.Matrix2:
                    return "Matrix2x2";
                case ConcreteSlotValueType.Matrix3:
                    return "Matrix3x3";
                case ConcreteSlotValueType.Matrix4:
                    return "Matrix4x4";
                case ConcreteSlotValueType.SamplerState:
                    return "SamplerState";
                default:
                    return "Error";
            }
        }

        public static PropertyType ConvertConcreteSlotValueTypeToPropertyType(ConcreteSlotValueType slotValue)
        {
            switch (slotValue)
            {
                case ConcreteSlotValueType.Texture2D:
                    return PropertyType.Texture;
                case ConcreteSlotValueType.Vector1:
                    return PropertyType.Float;
                case ConcreteSlotValueType.Vector2:
                    return PropertyType.Vector2;
                case ConcreteSlotValueType.Vector3:
                    return PropertyType.Vector3;
                case ConcreteSlotValueType.Vector4:
                    return PropertyType.Vector4;
                case ConcreteSlotValueType.Matrix2:
                    return PropertyType.Matrix2;
                case ConcreteSlotValueType.Matrix3:
                    return PropertyType.Matrix3;
                case ConcreteSlotValueType.Matrix4:
                    return PropertyType.Matrix4;
                case ConcreteSlotValueType.SamplerState:
                    return PropertyType.SamplerState;
                default:
                    return PropertyType.Vector4;
            }
        }

        public virtual void CollectPreviewMaterialProperties(List<PreviewProperty> properties)
        {
            var validSlots = GetInputSlots<MaterialSlot>().ToArray();

            for (var index = 0; index < validSlots.Length; index++)
            {
                var s = validSlots[index];
                var edges = owner.GetEdges(s.slotReference);
                if (edges.Any())
                    continue;

                var pp = new PreviewProperty
                {
                    m_Name = GetVariableNameForSlot(s.id),
                    m_PropType = ConvertConcreteSlotValueTypeToPropertyType(s.concreteValueType),
                    m_Vector4 = s.currentValue,
                    m_Float = s.currentValue.x,
                    m_Color = s.currentValue
                };
                properties.Add(pp);
            }
        }

        public virtual string GetVariableNameForSlot(int slotId)
        {
            var slot = FindSlot<MaterialSlot>(slotId);
            if (slot == null)
                throw new ArgumentException(string.Format("Attempting to use MaterialSlot({0}) on node of type {1} where this slot can not be found", slotId, this), "slotId");

            return GetVariableNameForNode() + "_" + slot.shaderOutputName;
        }

        public virtual string GetVariableNameForNode()
        {
            return name + "_" + GuidEncoder.Encode(guid);
        }

        public sealed override void AddSlot(ISlot slot)
        {
            if (!(slot is MaterialSlot))
                throw new ArgumentException(string.Format("Trying to add slot {0} to Material node {1}, but it is not a {2}", slot, this, typeof(MaterialSlot)));

            var addingSlot = (MaterialSlot)slot;
            var foundSlot = FindSlot<MaterialSlot>(slot.id);

            // this will remove the old slot and add a new one
            // if an old one was found. This allows updating values
            base.AddSlot(slot);

            if (foundSlot == null)
                return;

            // preserve the old current value.
            addingSlot.currentValue = foundSlot.currentValue;
        }
    }
}