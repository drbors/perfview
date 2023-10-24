﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using Microsoft.Diagnostics.Tracing.Parsers.GCDynamic;

namespace Microsoft.Diagnostics.Tracing.Parsers
{
    public sealed class GCDynamicTraceEventParser : TraceEventParser
    {
        private static readonly string ProviderName = "Microsoft-Windows-DotNETRuntime";
        internal static readonly Guid ProviderGuid = new Guid(unchecked((int)0xe13c0d23), unchecked((short)0xccbc), unchecked((short)0x4e12), 0x93, 0x1b, 0xd9, 0xcc, 0x2e, 0xee, 0x27, 0xe4);
        private static readonly Guid GCTaskGuid = new Guid(unchecked((int)0x044973cd), unchecked((short)0x251f), unchecked((short)0x4dff), 0xa3, 0xe9, 0x9d, 0x63, 0x07, 0x28, 0x6b, 0x05);

        private static volatile TraceEvent[] s_templates;

        public GCDynamicTraceEventParser(TraceEventSource source) : base(source)
        {
            // These registrations are required for raw (non-TraceLog sources).
            // They ensure that Dispatch is called so that the specific event handlers are called for each event.
            ((ITraceParserServices)source).RegisterEventTemplate(GCDynamicTemplate(Dispatch, GCDynamicEvent.RawDynamicTemplate));
            ((ITraceParserServices)source).RegisterEventTemplate(GCDynamicTemplate(Dispatch, GCDynamicEvent.HeapCountTuningTemplate));
        }

        protected override string GetProviderName()
        {
            return ProviderName;
        }

        protected internal override void EnumerateTemplates(Func<string, string, EventFilterResponse> eventsToObserve, Action<TraceEvent> callback)
        {
            if (s_templates == null)
            {
                var templates = new TraceEvent[2];

                // This template ensures that all GC dynamic events are parsed properly.
                templates[0] = GCDynamicTemplate(null, GCDynamicEvent.RawDynamicTemplate);

                // A template must be registered for each dynamic event type.  This ensures that after the event is converted
                // to its final form and saved in a TraceLog, that it can still be properly parsed and dispatched.
                templates[1] = GCDynamicTemplate(null, GCDynamicEvent.HeapCountTuningTemplate);

                s_templates = templates;
            }

            foreach (var template in s_templates)
                if (eventsToObserve == null || eventsToObserve(template.ProviderName, template.EventName) == EventFilterResponse.AcceptEvent)
                    callback(template);
        }

        /// <summary>
        /// Do not use.  This is here to avoid asserts that detect undeclared event templates.
        /// </summary>
        public event Action<GCDynamicTraceData> GCDynamicData
        {
            add
            {
                throw new NotSupportedException();
            }
            remove
            {
                throw new NotSupportedException();
            }
        }

        private event Action<GCDynamicTraceData> _gcHeapCountTuning;
        public event Action<GCDynamicTraceData> GCHeapCountTuning
        {
            add
            {
                _gcHeapCountTuning += value;
            }
            remove
            {
                _gcHeapCountTuning -= value;
            }
        }

        /// <summary>
        /// Responsible for dispatching the event after we determine its type
        /// and parse it.
        /// </summary>
        private void Dispatch(GCDynamicTraceData data)
        {
            if (_gcHeapCountTuning != null &&
                data.eventID == GCDynamicEvent.HeapCountTuningTemplate.ID)
            {
                _gcHeapCountTuning(data);
            }
        }

        private static GCDynamicTraceData GCDynamicTemplate(Action<GCDynamicTraceData> action, GCDynamicEvent eventTemplate)
        {
            Debug.Assert(eventTemplate != null);

            // action, eventid, taskid, taskName, taskGuid, opcode, opcodeName, providerGuid, providerName
            return new GCDynamicTraceData(action, (int) eventTemplate.ID, 1, eventTemplate.TaskName, GCTaskGuid, 41, eventTemplate.OpcodeName, ProviderGuid, ProviderName);
        }
    }
}

namespace Microsoft.Diagnostics.Tracing.Parsers.GCDynamic
{
    public sealed class GCDynamicTraceData : TraceEvent
    {
        internal GCDynamicTraceData(Action<GCDynamicTraceData> action, int eventID, int task, string taskName, Guid taskGuid, int opcode, string opcodeName, Guid providerGuid, string providerName)
            : base(eventID, task, taskName, taskGuid, opcode, opcodeName, providerGuid, providerName)
        {
            Action = action;
            NeedsFixup = true;
        }

        /// <summary>
        /// These are the raw payload fields of the underlying event.
        /// </summary>
        internal string Name { get { return GetUnicodeStringAt(0); } }
        internal Int32 DataSize { get { return GetInt32At(SkipUnicodeString(0)); } }
        internal byte[] Data { get { return GetByteArrayAt(offset: SkipUnicodeString(0) + 4, DataSize); } }
        internal int ClrInstanceID { get { return GetInt16At(SkipUnicodeString(0) + 4 + DataSize); } }

        /// <summary>
        /// This gets run before each event is dispatched.  It is responsible for detecting the event type
        /// and selecting the correct event template (derived from GCDynamicEvent).
        /// </summary>
        internal override void FixupData()
        {
            // Delete any per-event user data because we may mutate the event identity.
            EventTypeUserData = null;

            // Set the event identity.
            SelectEventMetadata();
        }

        protected internal override void Dispatch()
        {
            Action(this);
        }

        protected internal override Delegate Target
        {
            get { return Action; }
            set { Action = (Action<GCDynamicTraceData>)value; }
        }

        private HeapCountTuningTraceData _heapCountTuningTemplate = new HeapCountTuningTraceData();
        private RawDynamicTraceData _rawTemplate = new RawDynamicTraceData();

        /// <summary>
        /// Contains the fully parsed payload of the dynamic event.
        /// </summary>
        public GCDynamicEvent EventPayload
        {
            get
            {
                if (eventID == GCDynamicEvent.HeapCountTuningTemplate.ID)
                {
                    return _heapCountTuningTemplate.Bind(this);
                }

                return _rawTemplate.Bind(this);
            }
        }

        public override string[] PayloadNames
        {
            get
            {
                return EventPayload.PayloadNames;
            }
        }

        public override object PayloadValue(int index)
        {
            return EventPayload.PayloadValue(index);
        }

        public override StringBuilder ToXml(StringBuilder sb)
        {
            Prefix(sb);

            foreach (KeyValuePair<string, object> pair in EventPayload.PayloadValues)
            {
                XmlAttrib(sb, pair.Key, pair.Value);
            }

            sb.Append("</Event>");
            return sb;
        }

        private event Action<GCDynamicTraceData> Action;

        private void SelectEventMetadata()
        {
            GCDynamicEvent eventTemplate = GCDynamicEvent.RawDynamicTemplate;

            if (Name.Equals(GCDynamicEvent.HeapCountTuningTemplate.OpcodeName))
            {
                eventTemplate = GCDynamicEvent.HeapCountTuningTemplate;
            }

            SetMetadataFromTemplate(eventTemplate);
        }

        private unsafe void SetMetadataFromTemplate(GCDynamicEvent eventTemplate)
        {
            eventRecord->EventHeader.Id = (ushort)eventTemplate.ID;
            eventID = eventTemplate.ID;
            taskName = eventTemplate.TaskName;
            opcodeName = eventTemplate.OpcodeName;
            eventName = eventTemplate.EventName;
        }
    }

    /// <summary>
    /// Template base class for a specific type of dynamic event.
    /// </summary>
    public abstract class GCDynamicEvent
    {
        /// <summary>
        /// The list of specific event templates.
        /// </summary>
        internal static RawDynamicTraceData RawDynamicTemplate = new RawDynamicTraceData();
        internal static HeapCountTuningTraceData HeapCountTuningTemplate = new HeapCountTuningTraceData();

        /// <summary>
        /// Metadata that must be specified for each specific type of dynamic event.
        /// </summary>
        internal abstract TraceEventID ID { get; }
        internal abstract string TaskName { get; }
        internal abstract string OpcodeName { get; }
        internal abstract string EventName { get; }

        /// <summary>
        /// Properties and methods that must be implemented in order to integrate with the TraceEvent class.
        /// </summary>
        internal abstract string[] PayloadNames { get; }
        internal abstract object PayloadValue(int index);
        internal abstract IEnumerable<KeyValuePair<string, object>> PayloadValues { get; }

        /// <summary>
        /// The underlying TraceEvent object that is bound to the template during dispatch.
        /// It contains a pointer to the actual event payload and is what's used to fetch and parse fields.
        /// </summary>
        internal GCDynamicTraceData UnderlyingEvent { get; private set; }

        /// <summary>
        /// The Data field from the underlying event.
        /// </summary>
        internal byte[] DataField
        {
            get { return UnderlyingEvent.Data; }
        }

        /// <summary>
        /// Binds this template to an underlying event before it is dispatched.
        /// This is what allows the template to be used to parse the event.
        /// </summary>
        internal GCDynamicEvent Bind(GCDynamicTraceData underlyingEvent)
        {
            UnderlyingEvent = underlyingEvent;
            return this;
        }
    }

    internal sealed class RawDynamicTraceData : GCDynamicEvent
    {
        internal override TraceEventID ID => (TraceEventID)39;
        internal override string TaskName => "GC";
        internal override string OpcodeName => "DynamicData";
        internal override string EventName => "GC/DynamicData";

        private string[] _payloadNames;

        internal override string[] PayloadNames
        {
            get
            {
                if (_payloadNames == null)
                {
                    _payloadNames = new string[] { "Name", "DataSize", "Data", "ClrInstanceID" };
                }
                return _payloadNames;
            }
        }

        internal override object PayloadValue(int index)
        {
            switch (index)
            {
                case 0:
                    return UnderlyingEvent.Name;
                case 1:
                    return UnderlyingEvent.DataSize;
                case 2:
                    return UnderlyingEvent.Data;
                case 3:
                    return UnderlyingEvent.ClrInstanceID;
                default:
                    Debug.Assert(false, "Bad field index");
                    return null;
            }
        }

        internal override IEnumerable<KeyValuePair<string, object>> PayloadValues
        {
            get
            {
                yield return new KeyValuePair<string, object>("Name", UnderlyingEvent.Name);
                yield return new KeyValuePair<string, object>("DataSize", UnderlyingEvent.DataSize);
                yield return new KeyValuePair<string, object>("Data", string.Join(",", UnderlyingEvent.Data));
                yield return new KeyValuePair<string, object>("ClrInstanceID", UnderlyingEvent.ClrInstanceID);
            }
        }
    }

    internal sealed class HeapCountTuningTraceData : GCDynamicEvent
    {
        public short Version { get { return BitConverter.ToInt16(DataField, 0); } }
        public short NewHeapCount { get { return BitConverter.ToInt16(DataField, 2); } }
        public long GCIndex { get { return BitConverter.ToInt64(DataField, 4); } }
        public float MedianPercentOverhead { get { return BitConverter.ToSingle(DataField, 12); } }
        public float SmoothedMedianPercentOverhead { get { return BitConverter.ToSingle(DataField, 16); } }
        public float OverheadReductionPerStepUp { get { return BitConverter.ToSingle(DataField, 20); } }
        public float OverheadIncreasePerStepDown { get { return BitConverter.ToSingle(DataField, 24); } }
        public float SpaceCostIncreasePerStepUp { get { return BitConverter.ToSingle(DataField, 28); } }
        public float SpaceCostDecreasePerStepDown { get { return BitConverter.ToSingle(DataField, 32); } }

        internal override TraceEventID ID => TraceEventID.Illegal - 10;
        internal override string TaskName => "GC";
        internal override string OpcodeName => "HeapCountTuning";
        internal override string EventName => "GC/HeapCountTuning";

        private string[] _payloadNames;

        internal override string[] PayloadNames
        {
            get
            {
                if (_payloadNames == null)
                {
                    _payloadNames = new string[] { "Version", "NewHeapCount", "GCIndex", "MedianPercentOverhead", "SmoothedMedianPercentOverhead", "OverheadReductionPerStepUp", "OverheadIncreasePerStepDown", "SpaceCostIncreasePerStepUp", "SpaceCostDecreasePerStepDown" };
                }

                return _payloadNames;
            }
        }

        internal override object PayloadValue(int index)
        {
            switch (index)
            {
                case 0:
                    return Version;
                case 1:
                    return NewHeapCount;
                case 2:
                    return GCIndex;
                case 3:
                    return MedianPercentOverhead;
                case 4:
                    return SmoothedMedianPercentOverhead;
                case 5:
                    return OverheadReductionPerStepUp;
                case 6:
                    return OverheadIncreasePerStepDown;
                case 7:
                    return SpaceCostIncreasePerStepUp;
                case 8:
                    return SpaceCostDecreasePerStepDown;
                default:
                    Debug.Assert(false, "Bad field index");
                    return null;
            }
        }

        internal override IEnumerable<KeyValuePair<string, object>> PayloadValues
        {
            get
            {
                yield return new KeyValuePair<string, object>("Version", Version);
                yield return new KeyValuePair<string, object>("NewHeapCount", NewHeapCount);
                yield return new KeyValuePair<string, object>("GCIndex", GCIndex);
                yield return new KeyValuePair<string, object>("MedianPercentOverhead", MedianPercentOverhead);
                yield return new KeyValuePair<string, object>("SmoothedMedianPercentOverhead", SmoothedMedianPercentOverhead);
                yield return new KeyValuePair<string, object>("OverheadReductionPerStepUp", OverheadReductionPerStepUp);
                yield return new KeyValuePair<string, object>("OverheadIncreasePerStepDown", OverheadIncreasePerStepDown);
                yield return new KeyValuePair<string, object>("SpaceCostIncreasePerStepUp", SpaceCostIncreasePerStepUp);
                yield return new KeyValuePair<string, object>("SpaceCostDecreasePerStepDown", SpaceCostDecreasePerStepDown);
            }
        }
    }
}