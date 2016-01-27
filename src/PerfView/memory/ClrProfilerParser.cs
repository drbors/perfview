using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Collections.Generic;
using ClrProfiler;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing.Parsers;
using Address = System.UInt64;


// See code:#Overview for an Overview
namespace ClrProfiler
{
	#region Generic Profiling Data Structures that CLRProfilerParser uses
	public class Sample
	{
		public Sample(float metric, StackFrame stack)
		{
			this.stack = stack;
			this.metric = metric;
		}
		public StackFrame Stack
		{
			get { return stack; }
		}
		public float Metric
		{
			get { return metric; }
		}
		public override string ToString()
		{
			return "Sample " + metric.ToString("f1") + "\r\n" + stack;
		}

		#region PrivateFields
		private StackFrame stack;
		private float metric;
		#endregion
	};

	public class MemoryAccessSample : Sample
	{
		public enum AccessTypeKinds
		{
			Read,
			Write
		}

		public MemoryAccessSample(Address accessAddress, int accessSize, AccessTypeKinds accessType, float accessTimePercent, StackFrame stackTrace)
			: base(accessSize, stackTrace)
		{
			this.accessAddress = accessAddress;
			this.accessType = accessType;
			this.accessTimePercent = accessTimePercent;
		}

		public Address AccessAddress
		{
			get { return accessAddress; }
		}
		public int AccessSize
		{
			get { return (int)Metric; }
		}
		public AccessTypeKinds AccessType
		{
			get { return accessType; }
		}
		public float AccessTimePercent
		{
			get { return accessTimePercent; }
		}
		public override string ToString()
		{
			return "Sample 0x" + AccessAddress.ToString("x") + " Size " + AccessSize + "\r\n" + Stack;
		}

		#region PrivateFields
		private Address accessAddress;
		private AccessTypeKinds accessType;
		private float accessTimePercent;
		#endregion
	};

	abstract public class StackFrame
	{
		// This should be a fully qualified name, including signature.
		public abstract string Name { get; }
		// Returns null if the frame has no caller.  
		public abstract StackFrame Caller { get; }

		// The rest of these may be present, they have default 'not present' values if they are called.  
		public virtual string ModulePath
		{
			get { return ""; }
		}
		public virtual ulong CodeAddress
		{
			get { return 0; }
		}
		public virtual uint Offset
		{
			get { return 0xFFFFFFFF; }
		}
		public virtual uint LineNumber
		{
			get { return 0; }
		}
		public virtual string SourceFilePath
		{
			get { return ""; }
		}
		public override string ToString()
		{
			StringBuilder sb = new StringBuilder();
			StackFrame frame = this;
			while (frame != null)
			{
				sb.Append("    ").Append(frame.Name).AppendLine();
				frame = frame.Caller;
			}
			return sb.ToString();
		}
	}

	#endregion

	#region Utilities

	/// <summary>
	/// The is really what BinaryReader should have been... (sigh)
	/// 
	/// We need really fast, byte-by-byte streaming. ReadChar needs to be inliable .... All the routines that
	/// give back characters assume the bytes are ASCII (The translations from bytes to chars is simply a
	/// cast).
	/// 
	/// The basic model is that of a Enumerator. There is a 'Current' property that represents the current
	/// byte, and 'MoveNext' that moves to the next byte and returns false if there are no more bytes. Like
	/// Enumerators 'MoveNext' needs to be called at least once before 'Current' is valid.
	/// 
	/// Unlike standard Enumerators, FastStream does NOT consider it an error to read 'Current' is read when
	/// there are no more characters.  Instead Current returns a Sentinal value (by default this is 0, but
	/// the 'Sentinal' property allow you to choose it).   This is often more convenient and efficient to
	/// allow checking end-of-file (which is rare), to happen only at certain points in the parsing logic.  
	/// 
	/// Another really useful feature of this stream is that you can peek ahead efficiently a large number
	/// of bytes (since you read ahead into a buffer anyway).
	/// </summary>
	public sealed class FastStream
	{
		public FastStream(string filePath)
			: this(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
		{
		}
		public FastStream(Stream stream)
		{
			buffer = new byte[16384];
			historyBuffer = new byte[HistoryBufferLength];
			this.historyBufferCount = 0;
			this.historyBufferPosition = 0;
			bufferReadPos = 1;          // We make this 1, 1 initially so that EndOfStream works before MoveNext is called
			bufferFillPos = 1;
			this.stream = stream;
		}

		/// <summary>
		/// The stream position can't be used for compressed files, so we need to keep track of it
		/// </summary>
		public long Position { get; private set; }

		public struct MarkedPosition
		{
			internal byte[] buffer;
			internal uint bufferReadPos;
			internal uint bufferFillPos;
			internal long streamPos;

			public MarkedPosition(byte[] buffer, uint readPos, uint fillPos, long streamPos)
			{
				this.buffer = buffer;
				this.bufferReadPos = readPos;
				this.bufferFillPos = fillPos;
				this.streamPos = streamPos;
			}
		}

		public MarkedPosition MarkPosition()
		{
			byte[] tempBuffer = null;
			if (!markBufferUsed)
			{
				markBufferUsed = true;
				if (markBuffer == null)
					markBuffer = new byte[buffer.Length];
				tempBuffer = markBuffer;
			}
			else
			{
				tempBuffer = new byte[bufferFillPos];
			}
			Array.Copy(buffer, tempBuffer, bufferFillPos);
			return new MarkedPosition(tempBuffer, bufferReadPos, bufferFillPos, this.Position);
		}

		public void RestoreToMark(MarkedPosition position)
		{

			if (HistoryBufferLength < this.Position - position.streamPos + 1)
			{
				this.stream.Position = position.streamPos;
				this.historyBufferPosition = 0;
				this.historyBufferCount = 0;
			}
			else
			{
				this.historyBufferCount = this.historyBufferPosition = (int)(this.Position - position.streamPos + 1);
			}

			this.Position = position.streamPos;

			bufferFillPos = position.bufferFillPos;
			bufferReadPos = position.bufferReadPos;
			Array.Copy(position.buffer, buffer, bufferFillPos);
			if (markBufferUsed)
			{
				if (Object.ReferenceEquals(position.buffer, buffer))
					markBufferUsed = false;
			}

			this.IncrementHistoryBuffer();
		}

		public byte Current
		{
			get
			{
				if (this.historyBufferCount > 0)
				{
					return this.historyBuffer[this.historyBufferPosition];
				}
				return buffer[bufferReadPos];
			}
		}

		private void IncrementHistoryBuffer()
		{
			byte[] historyBufferCopy = new byte[HistoryBufferLength];
			this.historyBuffer.CopyTo(historyBufferCopy, 0);
			for (int i = 1; i < this.historyBuffer.Length; i++)
			{
				this.historyBuffer[i] = historyBufferCopy[i - 1];
			};
			this.historyBuffer[0] = this.Current;
			return;
		}

		public bool MoveNext()
		{
			IncReadPos();
			this.Position++;
			this.historyBufferCount = this.historyBufferCount < 1 ? 0 : this.historyBufferCount - 1;
			bool ret = true;
			if (bufferReadPos >= bufferFillPos)
				ret = MoveNextHelper();

			this.IncrementHistoryBuffer();

#if DEBUG
            nextChars = Encoding.Default.GetString(buffer, (int)bufferReadPos, Math.Min(40, buffer.Length - (int)bufferReadPos));
#endif
			return ret;
		}
		public byte ReadChar()
		{
			MoveNext();
			return Current;
		}
		public int ReadInt()
		{
			byte c = Current;
			while (c == ' ')
				c = ReadChar();
			bool negative = false;
			if (c == '-')
			{
				negative = true;
				c = ReadChar();
			}
			if (c >= '0' && c <= '9')
			{
				int value = 0;
				if (c == '0')
				{
					c = ReadChar();
					if (c == 'x' || c == 'X')
					{
						MoveNext();
						value = ReadHex();
					}
				}
				while (c >= '0' && c <= '9')
				{
					value = value * 10 + c - '0';
					c = ReadChar();
				}

				if (negative)
					value = -value;
				return value;
			}
			else
			{
				return -1;
			}
		}
		public uint ReadUInt()
		{
			return (uint)ReadInt();
		}
		public long ReadLong()
		{
			byte c = Current;
			while (c == ' ')
				c = ReadChar();
			bool negative = false;
			if (c == '-')
			{
				negative = true;
				c = ReadChar();
			}
			if (c >= '0' && c <= '9')
			{
				long value = 0;
				if (c == '0')
				{
					c = ReadChar();
					if (c == 'x' || c == 'X')
					{
						MoveNext();
						value = ReadLongHex();
					}
				}
				while (c >= '0' && c <= '9')
				{
					value = value * 10 + c - '0';
					c = ReadChar();
				}

				if (negative)
					value = -value;
				return value;
			}
			else
			{
				return -1;
			}
		}
		public ulong ReadULong()
		{
			return (ulong)ReadLong();
		}
		public bool EndOfStream { get { return bufferFillPos == 0; } }
		public void ReadAsciiStringUpTo(char endMarker, StringBuilder sb)
		{
			for (;;)
			{
				byte c = Current;
				if (c == endMarker)
					break;
				sb.Append((char)c);
				if (!MoveNext())
					break;
			}
		}
		public void ReadAsciiStringUpTo(string endMarker, StringBuilder sb)
		{
			Debug.Assert(0 < endMarker.Length);
			for (;;)
			{
				ReadAsciiStringUpTo(endMarker[0], sb);
				uint markerIdx = 1;
				for (;;)
				{
					if (markerIdx >= endMarker.Length)
						return;
					if (Peek(markerIdx) != endMarker[(int)markerIdx])
						break;
					markerIdx++;
				}
				MoveNext();
			}
		}
		public void SkipUpTo(char endMarker)
		{
			while (Current != endMarker)
			{
				if (!MoveNext())
					break;
			}
		}
		public void SkipSpace()
		{
			while (Current == ' ')
				MoveNext();
		}
		public void SkipWhiteSpace()
		{
			while (Char.IsWhiteSpace((char)Current))
				MoveNext();
		}
		/// <summary>
		/// Reads the string into the stringBuilder until a byte is read that
		/// is one of the characters in 'endMarkers'.  
		/// </summary>
		public void ReadAsciiStringUpToAny(string endMarkers, StringBuilder sb)
		{
			for (;;)
			{
				byte c = Current;
				for (int i = 0; i < endMarkers.Length; i++)
					if (c == endMarkers[i])
						return;
				sb.Append((char)c);
				if (!MoveNext())
					break;
			}
		}

		/// <summary>
		/// Returns a number of bytes ahead without advancing the pointer. 
		/// Peek(0) is the same as calling Current.  
		/// </summary>
		/// <param name="bytesAhead"></param>
		/// <returns></returns>
		public byte Peek(uint bytesAhead)
		{
			uint index = bytesAhead + bufferReadPos;
			if (index >= bufferFillPos)
				index = PeekHelper(bytesAhead);

			return buffer[index];
		}

		public Stream BaseStream { get { return stream; } }
		/// <summary>
		/// For efficient reads, we allow you to read Current past the end of the stream.  You will
		/// get the 'Sentinal' value in that case.  This defaults to 0, but you can change it if 
		/// there is a better 'rare' value to use as an end of stream marker.  
		/// </summary>
		public byte Sentinal = 0;

		#region privateMethods
		private bool MoveNextHelper()
		{
			bufferReadPos = 0;
			bufferFillPos = (uint)stream.Read(buffer, 0, buffer.Length);
			if (bufferFillPos < buffer.Length)
				buffer[bufferFillPos] = Sentinal;       // we define 0 as the value you get after EOS.  
			return (bufferFillPos > 0);
		}

		private const int HistoryBufferLength = byte.MaxValue;
		private int historyBufferPosition;
		private int historyBufferCount;
		private byte[] historyBuffer;

		private uint PeekHelper(uint bytesAhead)
		{
			if (bytesAhead >= buffer.Length)
				throw new Exception("Can only peek ahead the length of the buffer");

			// Copy down the remaining characters. 
			bufferFillPos = bufferFillPos - bufferReadPos;
			for (uint i = 0; i < bufferFillPos; i++)
				buffer[i] = buffer[bufferReadPos + i];
			bufferReadPos = 0;

			// Fill up the buffer as much as we can.  
			for (;;)
			{
				uint count = (uint)stream.Read(buffer, (int)bufferFillPos, buffer.Length - (int)bufferFillPos);
				bufferFillPos += count;
				if (bufferFillPos < buffer.Length)
					buffer[bufferFillPos] = Sentinal;

				if (bufferFillPos > bytesAhead)
					break;
				if (count == 0)
					break;
			}
			return bytesAhead;
		}

		// Only here to 'trick' the JIT compiler into inlining MoveNext.  (we were a bit over the 32 byte IL limit). 
		private void IncReadPos()
		{
			bufferReadPos++;
		}

		public int ReadHex()
		{
			int value = 0;
			while (true)
			{
				int digit = Current;
				if (digit >= '0' && digit <= '9')
					digit -= '0';
				else if (digit >= 'a' && digit <= 'f')
					digit -= 'a' - 10;
				else if (digit >= 'A' && digit <= 'F')
					digit -= 'A' - 10;
				else
					return value;
				MoveNext();
				value = value * 16 + digit;
			}
		}

		public long ReadLongHex()
		{
			long value = 0;
			while (true)
			{
				int digit = Current;
				if (digit >= '0' && digit <= '9')
					digit -= '0';
				else if (digit >= 'a' && digit <= 'f')
					digit -= 'a' - 10;
				else if (digit >= 'A' && digit <= 'F')
					digit -= 'A' - 10;
				else
					return value;
				MoveNext();
				value = value * 16 + digit;
			}
		}

		#endregion
		#region privateState
		byte[] markBuffer;
		bool markBufferUsed;
		readonly byte[] buffer;
		uint bufferReadPos;      // The next character to read
		uint bufferFillPos;      // The last character in the buffer that is valid
		Stream stream;
#if DEBUG
        string nextChars;
        public override string ToString()
        {
            return nextChars;
        }
#endif
		#endregion
	}

	public static class ArrayUtilities<T>
	{
		public static T[] InsureCapacity(T[] array, uint desiredValidIndex)
		{
			if (array == null)
				array = new T[Math.Max(16, desiredValidIndex + 1)];
			else if (desiredValidIndex >= (uint)array.Length)
			{
				desiredValidIndex = Math.Max(desiredValidIndex, (uint)array.Length * 2);
				T[] newArray = new T[desiredValidIndex];
				Array.Copy(array, newArray, array.Length);
				array = newArray;
			}
			return array;
		}
	}

	#endregion

	/// <summary>
	/// #Overview
	/// 
	/// A ClrProfilerParser knows how to parse a CLRProfiler log file generated by ClrProfiler.exe Its job is
	/// to understand the file format and to the really basic decoding/decompression so that the upper level
	/// software can interpret stacks easily. What this means in practice is that ClrProfilerParser is
	/// responsible for decoding the stack trace information.
	/// 
	/// Some attempt was made to make this very efficient. It should be able to handle Gig+ files without too
	/// much trouble.
	/// 
	/// The basic model is that ClrProfilerParser has an event for every Profiler event in the log file.
	/// The user creates a 'CLrProfilerParser' subscribes to the events of interest and then calls ReadFile
	/// which will cause the events to fire.    
	/// 
	/// See the (#if'ed out) very small sample code:#SampleProgram at the bottom of this file for an actual
	/// use example.
	/// </summary>
	public sealed class ClrProfilerParser
	{
		public ClrProfilerParser() { }

		// The important callbacks EventHandler(entering a call, and allocating a GC heap object);
		public delegate void AllocationEventHandler(ProfilerAllocID allocId, Address objectAddress, uint threadId);
		public event AllocationEventHandler Allocation;

		public delegate void CallEventHandler(ProfilerStackTraceID stackId, uint threadId);
		public event CallEventHandler Call;

		// If over 1 msec has elapse since last tick, the next event will also trigger another tick event.  ;
		// TODO confirm the statement above is true.    
		public delegate void TickEventHandler(int milliSecondsSinceStart);
		public event TickEventHandler Tick;

		// Other interesting events;
		public delegate void GCEventHandler(int gcNumber, bool induced, int condemnedGeneration, List<ProfilerGCSegment> gcMemoryRanges);
		public event GCEventHandler GCStart;
		public event GCEventHandler GCEnd;

		public delegate void ModuleLoadEventHandler(ProfilerModule module);
		public event ModuleLoadEventHandler ModuleLoad;
		public delegate void AssemblyLoadEventHandler(string assemblyName, Address assemblyAddress, uint threadId);
		public event AssemblyLoadEventHandler AssemblyLoad;
		public delegate void CommentEventHandler(string comment);
		public event CommentEventHandler UserEvent;

		// GC Handle information 
		public delegate void CreateHandleEventHandler(Address handle, Address objectInHandle, ProfilerStackTraceID stackId, uint threadId);
		public event CreateHandleEventHandler CreateHandle;
		public delegate void DestroyHandleEventHandler(Address handle, ProfilerStackTraceID stackId, uint threadId);
		public event DestroyHandleEventHandler DestroyHandle;

		// Information for analyzing the heap in a fine grained fashion EventHandler(following points-to-graph); 
		public delegate void HeapDumpEventHandler(List<Address> roots);
		public event HeapDumpEventHandler HeapDump;
		public delegate void ObjectDescriptionEventHandler(Address objectAddress, ProfilerTypeID typeId, uint size, List<Address> pointsTo);
		public event ObjectDescriptionEventHandler ObjectDescription;
		// Objects in this range moved. 
		public delegate void RelocationEventHandler(Address oldBase, Address newBase, uint size);
		public event RelocationEventHandler ObjectRangeRelocation;
		// Objects in this range are still alive. 
		public delegate void LiveObjectRangeHandler(Address startAddress, uint size);
		public event LiveObjectRangeHandler ObjectRangeLive;
		// This object was finalized
		public delegate void FinalizerEventHandler(Address objectAddress, bool isCritical);
		public event FinalizerEventHandler Finalizer;
		public delegate void GCRootEventHandler(Address objectAddress, GcRootKind rootKind, GcRootFlags rootFlags, Address rootID);
		public event GCRootEventHandler GCRoot;

		public delegate void StaticVarEventHandler(Address objectAddress, string fieldName, ProfilerTypeID typeID, uint threadID, string appDomainName);
		public event StaticVarEventHandler StaticVar;

		// TODO should we show the whole stack?
		public delegate void LocalVarEventHandler(Address objectAddress, string localVarName, string methodName, ProfilerTypeID typeID, uint threadID, string appDomainName);
		public event LocalVarEventHandler LocalVar;

		void ReadFile(FastStream stream)
		{
			StringBuilder sb = new StringBuilder();
			List<Address> addressList = new List<Address>();
			int lineNum = 1;
			int gcNumber = 0;

			stream.MoveNext();
			for (;;)
			{
				// Check whether we've been asked to abort this parsing early
				if (abort)
					return;

				byte c = stream.Current;
				stream.MoveNext();
				// Because the stats are highly skewed, and if-then-else tree is better than a switch.  
				if (c == '\r')
				{
					// Do nothing 
				}
				else if (c == '\n')
				{
					lineNum++;
					if (lineNum % 4096 == 0)                 // Every 4K events allow Thread.Interrupt.  
						System.Threading.Thread.Sleep(0);
				}
				else if (c == 'c')
				{
					// Call was made
					if (Call == null)
						goto SKIP_LINE;

					uint threadId = stream.ReadUInt();
					uint fileStackId = stream.ReadUInt();
					if (!stream.EndOfStream)
					{
						ProfilerStackTraceID stackId = GetStackIdForFileId(fileStackId);
						Call(stackId, threadId);
					}
				}
				else if (c == '!')
				{
					// Allocation was made
					if (Allocation == null)
						goto SKIP_LINE;

					uint threadId = stream.ReadUInt();
					Address address = (Address)stream.ReadULong();
					uint fileStackId = stream.ReadUInt();
					ProfilerAllocID allocId = GetAllocIdForFileId(fileStackId);
					if (!stream.EndOfStream)
						Allocation(allocId, address, threadId);
				}
				else if (c == 'n')
				{
					// Announce a stack
					uint fileStackId = stream.ReadUInt();
					uint flag = stream.ReadUInt();
					uint hadTypeId = (flag & 2);
					uint tailCount = flag >> 2;

					uint hasTypeId = (flag & 1);
					ProfilerTypeID typeId = 0;
					uint size = 0;
					if (hasTypeId != 0)
					{
						typeId = (ProfilerTypeID)stream.ReadUInt();
						size = stream.ReadUInt();
					}
					uint fileTailStackId = 0;
					if (tailCount > 0)
						fileTailStackId = stream.ReadUInt();

					if (!stream.EndOfStream)
					{
						// The rest of the line are a series of function Ids (going from closest to top of stack
						// to closest to execution.  
						ProfilerStackTraceID stackId = GetStackIdForFileId(fileTailStackId);
						if (tailCount > 0)
							stackId = GetTopOfStack(stackId, tailCount);
						for (;;)
						{
							ProfilerMethodID methodId = (ProfilerMethodID)stream.ReadUInt();
							if ((uint)methodId == 0xFFFFFFFF)
								break;

							// TODO decide how to fix this. 
							// Debug.Assert(methodIds[(int)methodId].name != null);

							uint newStackId = stackIdLimit++;
							stackIds = ArrayUtilities<StackInfo>.InsureCapacity(stackIds, newStackId);
							stackIds[newStackId].methodId = methodId;
							stackIds[newStackId].stackId = stackId;

							stackId = (ProfilerStackTraceID)newStackId;
							if (methodIds[(int)methodId] != null)
								VerboseDebug("Frame stackId=S" + newStackId + " name=" + methodIds[(int)methodId].name);
						}
						if (hasTypeId != 0)
						{
							uint allocId = allocIdLimit++;
							allocIds = ArrayUtilities<AllocInfo>.InsureCapacity(allocIds, allocId);
							allocIds[allocId].Set(typeId, size, stackId);

							if (methodIds[(int)stackIds[(int)stackId].methodId] != null)    // TODO 
								VerboseDebug("FileAlloc s" + fileStackId + " maps to allocId=" + allocId +
									" type=" + typeIds[(int)typeId].name + " size=" + size + " stackId=" + stackId +
									((stackId == 0) ? "" : " (" + methodIds[(int)stackIds[(int)stackId].methodId].name + ")"));
							fileIdStackInfoId = ArrayUtilities<uint>.InsureCapacity(fileIdStackInfoId, fileStackId);
							fileIdStackInfoId[fileStackId] = SetAllocId(allocId);
						}
						else
						{
							if (methodIds[(int)stackIds[(int)stackId].methodId] != null)    // TODO 
								VerboseDebug("FileStack s" + fileStackId + " maps to stackId=" + stackId +
									((stackId == 0) ? "" : " (" + methodIds[(int)stackIds[(int)stackId].methodId].name + ")"));
							fileIdStackInfoId = ArrayUtilities<uint>.InsureCapacity(fileIdStackInfoId, fileStackId);
							fileIdStackInfoId[fileStackId] = SetStackId(stackId);
						}
					}
				}
				else if (c == 'f')
				{
					// Announce a function (used in stacks)
					ProfilerMethodID methodId = (ProfilerMethodID)stream.ReadUInt();
					stream.SkipSpace();
					sb.Length = 0;
					// name may contain spaces if they are in angle brackets.
					// Example: <Module>::std_less<unsigned void>.()
					// The name may be truncated at 255 chars by profilerOBJ.dll
					int angleBracketsScope = 0;
					c = stream.Current;
					while (c > ' ' || angleBracketsScope != 0 && sb.Length < 255)
					{
						if (c == '<')
							angleBracketsScope++;

						sb.Append((char)c);
						c = stream.ReadChar();

						if (c == '>' && angleBracketsScope > 0)
							angleBracketsScope--;
					}
					string name = sb.ToString();
					stream.SkipSpace();
					sb.Length = 0;
					c = stream.Current;
					while (c > '\r')
					{
						sb.Append((char)c);
						if (c == ')')
						{
							c = stream.ReadChar();
							break;
						}
						c = stream.ReadChar();
					}
					string signature = sb.ToString();
					Address address = (Address)stream.ReadULong();
					uint size = stream.ReadUInt();
					ProfilerMethodID moduleId = (ProfilerMethodID)stream.ReadUInt();
					uint fileFirstStackId = stream.ReadUInt();
					if (!stream.EndOfStream)
					{
						ProfilerModule module = null;
						if (methodId == 0)      // Hack for first method, it does not have a module ID or a stack
						{
							moduleId = 0;
							fileFirstStackId = 0;
							module = new ProfilerModule(0, "UnknownModule", 0, 0);
						}
						else
							module = moduleIds[(int)moduleId];
						ProfilerStackTraceID firstStackId = GetStackIdForFileId(fileFirstStackId);
						VerboseDebug("Method " + methodId + "  name=" + name + " sig=" + signature +
							" moduleId=" + moduleId + " size=" + size + " stack=S" + firstStackId);
						methodIds = ArrayUtilities<ProfilerMethod>.InsureCapacity(methodIds, (uint)methodId);
						methodIds[(int)methodId] = new ProfilerMethod(methodId, name, signature, address, size, module, firstStackId);
						if ((uint)methodId >= methodIdLimit)
							methodIdLimit = (uint)methodId + 1;
					}
				}
				else if (c == 't')
				{
					// Announce a nodeId
					ProfilerTypeID typeId = (ProfilerTypeID)stream.ReadUInt();
					bool isFinalizable = (stream.ReadInt() == 1);
					stream.SkipSpace();
					sb.Length = 0;
					stream.ReadAsciiStringUpTo('\r', sb);
					string name = sb.ToString();
					if (!stream.EndOfStream)
					{
						VerboseDebug("Type " + typeId + " name=" + name + " isFinalizable=" + isFinalizable);
						CreateType(typeId, name, isFinalizable);
					}
				}
				else if (c == 'h')
				{
					// A handle was created 
					if (CreateHandle == null)
						goto SKIP_LINE;

					uint threadId = stream.ReadUInt();
					Address handle = (Address)stream.ReadULong();
					Address objectInHandle = (Address)stream.ReadULong();
					uint fileStackId = stream.ReadUInt();
					if (!stream.EndOfStream && CreateHandle != null)
					{
						ProfilerStackTraceID stackId = GetStackIdForFileId(fileStackId);
						CreateHandle(handle, objectInHandle, stackId, threadId);
					}
				}
				else if (c == 'i')
				{
					// Time has gone by (do this every msec to 10 msec 
					if (Tick == null)
						goto SKIP_LINE;

					int millisecondsSinceStart = stream.ReadInt();
					if (!stream.EndOfStream)
						Tick(millisecondsSinceStart);
				}
				else if (c == 'j')
				{
					// Destroy a handle
					if (DestroyHandle == null)
						goto SKIP_LINE;

					uint threadId = stream.ReadUInt();
					Address handle = (Address)stream.ReadULong();
					uint fileStackId = stream.ReadUInt();
					if (!stream.EndOfStream)
					{
						ProfilerStackTraceID stackId = GetStackIdForFileId(fileStackId);
						DestroyHandle(handle, stackId, threadId);
					}
				}
				else if (c == 'l')
				{
					// Finalizer called 
					if (Finalizer == null)
						goto SKIP_LINE;

					Address objectAddress = (Address)stream.ReadULong();
					bool isCritical = (stream.ReadInt() == 1);
					if (!stream.EndOfStream)
						Finalizer(objectAddress, isCritical);
				}
				else if (c == 'b')
				{
					// GC boundary (first args indicates if it is start or end)
					if (GCStart == null && GCEnd == null)
						goto SKIP_LINE;

					bool gcStart = (stream.ReadInt() == 1);
					bool induced = (stream.ReadInt() == 1);
					int condemnedGeneration = stream.ReadInt();

					if (gcStart)
						gcNumber++;

					List<ProfilerGCSegment> gcMemoryRanges = new List<ProfilerGCSegment>(5);
					for (;;)
					{
						ProfilerGCSegment range = new ProfilerGCSegment((Address)stream.ReadULong(), stream.ReadUInt(), stream.ReadUInt(), stream.ReadInt());
						if (range.rangeGeneration < 0)
							break;

						gcMemoryRanges.Add(range);
					}
					if (!stream.EndOfStream)
					{
						if (gcStart && GCStart != null)
							GCStart(gcNumber, induced, condemnedGeneration, gcMemoryRanges);
						if (!gcStart && GCEnd != null)
							GCEnd(gcNumber, induced, condemnedGeneration, gcMemoryRanges);
					}
				}
				else if (c == 'g')
				{
					// Generation count
					int gen0Count = stream.ReadInt();
					int gen1Count = stream.ReadInt();
					int gen2Count = stream.ReadInt();
				}
				else if (c == 'r')
				{
					// Heap roots (for heap dump)
					if (HeapDump == null && ObjectDescription == null)
						goto SKIP_LINE;

					addressList.Clear();
					for (;;)
					{
						Address root = (Address)stream.ReadULong();
						if (root == BadAddress)
							break;
						addressList.Add(root);
					}
					if (!stream.EndOfStream && HeapDump != null)
						HeapDump(addressList);
				}
				else if (c == 'o')
				{
					// Object in heap (heap dump)
					if (ObjectDescription == null && HeapDump == null)
						goto SKIP_LINE;

					Address objectAddress = (Address)stream.ReadULong();
					ProfilerTypeID typeId = (ProfilerTypeID)stream.ReadUInt();
					uint size = stream.ReadUInt();

					addressList.Clear();
					for (;;)
					{
						Address reference = (Address)stream.ReadULong();
						if (reference == BadAddress)
							break;
						addressList.Add(reference);
					}
					if (!stream.EndOfStream)
						ObjectDescription(objectAddress, typeId, size, addressList);
				}
				else if (c == 'u')
				{
					// Object relocation 
					if (ObjectRangeRelocation == null)
						goto SKIP_LINE;

					Address oldBase = (Address)stream.ReadULong();
					Address newBase = (Address)stream.ReadULong();
					uint size = stream.ReadUInt();

					if (!stream.EndOfStream)
						ObjectRangeRelocation(oldBase, newBase, size);
				}
				else if (c == 'v')
				{
					// Object live range 
					if (ObjectRangeLive == null)
						goto SKIP_LINE;

					Address startAddress = (Address)stream.ReadULong();
					uint size = stream.ReadUInt();
					if (!stream.EndOfStream)
						ObjectRangeLive(startAddress, size);
				}
				else if (c == 'm')
				{
					// Module load
					uint moduleId = stream.ReadUInt();
					sb.Length = 0;
					stream.ReadAsciiStringUpTo(" 0x", sb);
					string name = sb.ToString();
					Address address = (Address)stream.ReadULong();
					uint fileFirstStackId = stream.ReadUInt();
					if (!stream.EndOfStream)
					{
						ProfilerStackTraceID firstStackId = GetStackIdForFileId(fileFirstStackId);
						VerboseDebug("Module " + moduleId + " Addr=" + address + " stack=S" + firstStackId + " name=" + name);
						ProfilerModule newModule = CreateModule((ProfilerModuleID)moduleId, name, address, firstStackId);
						if (ModuleLoad != null)
							ModuleLoad(newModule);
					}
				}
				else if (c == 'y')
				{
					// Assembly load
					if (AssemblyLoad == null)
						goto SKIP_LINE;

					uint threadId = stream.ReadUInt();
					Address assemblyAddress = (Address)stream.ReadULong();
					sb.Length = 0;
					stream.SkipSpace();
					stream.ReadAsciiStringUpTo('\r', sb);
					string assemblyName = sb.ToString();

					if (!stream.EndOfStream && AssemblyLoad != null)
						AssemblyLoad(assemblyName, assemblyAddress, threadId);
				}
				else if (c == 'e')
				{
					// GC root (handle) (heap dump)
					if (GCRoot == null)
						goto SKIP_LINE;

					Address objectAddress = (Address)stream.ReadULong();
					GcRootKind rootKind = (GcRootKind)stream.ReadInt();
					GcRootFlags rootFlags = (GcRootFlags)stream.ReadInt();
					Address rootID = (Address)stream.ReadULong();

					if (!stream.EndOfStream && GCRoot != null)
						GCRoot(objectAddress, rootKind, rootFlags, rootID);
				}
				else if (c == 's')      // NEW! static variable root.   
				{
					if (StaticVar == null)
						goto SKIP_LINE;
					Address objectAddress = (Address)stream.ReadULong();

					stream.SkipSpace();
					sb.Length = 0;
					stream.ReadAsciiStringUpTo(' ', sb);
					string fieldName = sb.ToString();

					stream.SkipSpace();
					sb.Length = 0;
					stream.ReadAsciiStringUpTo(' ', sb);
					string typeName = sb.ToString();

					var threadId = stream.ReadUInt();

					stream.SkipSpace();
					sb.Length = 0;
					stream.ReadAsciiStringUpTo(' ', sb);
					string appDomainName = sb.ToString();


					// Find the typeId for this type name, and when you find it, call the callback.  
					// Use the TypesNeedingModule dictionary to avoid searching most of the list
					var typeIds = GetTypesNeedingModule(typeName, true);
					foreach (var typeId in typeIds)
					{
						var type = GetTypeById(typeId);
						if (type.name == typeName)
						{
							StaticVar(objectAddress, fieldName, typeId, threadId, appDomainName);
							break;
						}
					}
				}
				else if (c == 'L')      // NEW! local variable root.   
				{
					if (LocalVar == null)
						goto SKIP_LINE;
					Address objectAddress = (Address)stream.ReadULong();

					stream.SkipSpace();
					sb.Length = 0;
					stream.ReadAsciiStringUpTo(' ', sb);
					string localName = sb.ToString();

					stream.SkipSpace();
					sb.Length = 0;
					stream.ReadAsciiStringUpTo(' ', sb);
					string methodName = sb.ToString();

					stream.SkipSpace();
					sb.Length = 0;
					stream.ReadAsciiStringUpTo(' ', sb);
					string typeName = sb.ToString();

					var threadId = stream.ReadUInt();

					stream.SkipSpace();
					sb.Length = 0;
					stream.ReadAsciiStringUpTo(' ', sb);
					string appDomainName = sb.ToString();

					// Find the typeId for this type name, and when you find it, call the callback.  
					// Use the TypesNeedingModule dictionary to avoid searching most of the list
					var typeIds = GetTypesNeedingModule(typeName, true);
					foreach (var typeId in typeIds)
					{
						var type = GetTypeById(typeId);
						if (type.name == typeName)
						{
							LocalVar(objectAddress, localName, methodName, typeId, threadId, appDomainName);
							break;
						}
					}
				}
				else if (c == 'M')      // NEW! module information for types
				{
					stream.SkipSpace();
					sb.Length = 0;
					stream.ReadAsciiStringUpTo(' ', sb);
					string typeName = sb.ToString();

					stream.SkipSpace();
					sb.Length = 0;
					stream.ReadAsciiStringUpTo('\r', sb);
					string moduleName = sb.ToString();

					// Only add module names to type names that we already care about. 
					var typeIds = GetTypesNeedingModule(typeName, false);
					if (typeIds != null)
					{
						var moduleId = GetModuleIdForName(moduleName);
						foreach (var typeId in typeIds)
							GetTypeById(typeId).ModuleId = moduleId;
					}
				}
				else if (c == 'z')
				{
					// User event
					if (UserEvent == null)
						goto SKIP_LINE;

					sb.Length = 0;
					stream.SkipSpace();
					stream.ReadAsciiStringUpTo('\r', sb);
					string comment = sb.ToString();
					if (!stream.EndOfStream && UserEvent != null)
						UserEvent(comment);
				}
				else
				{
					// We matched none of the expected chars
					if (stream.EndOfStream)
						break;
					Debug.WriteLine("Warning unknown CLRProfiler entry line '" + (char)c + "' on line " + lineNum);
					goto SKIP_LINE;
				}

				continue;
				SKIP_LINE:
				stream.SkipUpTo('\r');
			}

			moduleNameToId = null;
			typesNeedingModule = null;
		}

		private ProfilerModule CreateModule(ProfilerModuleID moduleId, string moduleName, Address address, ProfilerStackTraceID firstStackId)
		{
			moduleIds = ArrayUtilities<ProfilerModule>.InsureCapacity(moduleIds, (uint)moduleId);
			ProfilerModule newModule = new ProfilerModule(moduleId, moduleName, address, firstStackId);

			if (moduleNameToId != null)
				moduleNameToId[moduleName] = (int)moduleId;

			moduleIds[(int)moduleId] = newModule;
			if ((uint)moduleId >= moduleIdLimit)
				moduleIdLimit = (uint)moduleId + 1;
			return newModule;
		}

		private void CreateType(ProfilerTypeID typeId, string typeName, bool isFinalizable)
		{
			typeIds = ArrayUtilities<ProfilerType>.InsureCapacity(typeIds, (uint)typeId);
			typeIds[(int)typeId] = new ProfilerType(this, typeId, typeName, isFinalizable);

			AddTypeToTypesNeedingModule(typeId, typeName);

			if ((uint)typeId >= typeIdLimit)
				typeIdLimit = (uint)typeId + 1;
		}

		private void AddTypeToTypesNeedingModule(ProfilerTypeID typeId, string typeName)
		{
			if (typesNeedingModule != null)
			{
				var typeKey = GetKey(typeName);

				List<ProfilerTypeID> typeNeedingModule;
				if (!typesNeedingModule.TryGetValue(typeKey, out typeNeedingModule))
				{
					typeNeedingModule = new List<ProfilerTypeID>();
					typesNeedingModule[typeKey] = typeNeedingModule;
				}
				typeNeedingModule.Add(typeId);
			}
		}

		private static string GetKey(string typeName)
		{
			// TODO FIX NOW, decide what to do about normalizing type names
			var typeKey = ProfilerType.FixGenerics(typeName);
			typeKey = Regex.Replace(typeKey, @" *<.*", "");               // remove generic parameters
			typeKey = Regex.Replace(typeKey, @"`\d+", "");                 // remove generic arity
			typeKey = Regex.Replace(typeKey, @"\[.*", "");                 // remove array spec
			return typeKey;
		}

		/// <summary>
		/// Gets the Stack ID for the top 'frameCount' frames. 
		/// </summary>
		private ProfilerStackTraceID GetTopOfStack(ProfilerStackTraceID stackId, uint frameCount)
		{
			// TODO this could be more efficient.  
			uint depth = 0;
			var curFrame = stackId;
			while (curFrame != ProfilerStackTraceID.Null)
			{
				// See if have a remembered value
				if (curFrame == m_cache_stackId)
				{
					depth += m_cache_depth;
					break;
				}
				curFrame = NextFrame(curFrame);
				depth++;
			}

			Debug.Assert(frameCount <= depth);
			int diff = (int)depth - (int)frameCount;
			curFrame = stackId;
			while (diff > 0)
			{
				curFrame = NextFrame(curFrame);
				--diff;
			}

			// Update the cache  
			m_cache_depth = frameCount;
			m_cache_stackId = curFrame;
			return curFrame;
		}
		// Used to speed up GetTopOfStack
		private ProfilerStackTraceID m_cache_stackId;
		private uint m_cache_depth;

		/// <summary>
		/// Actually read in a Profiler log file and call the any events the user subscribed to.  
		/// </summary>
		/// <param name="filePath"></param>
		public void ReadFile(string filePath)
		{
			abort = false;
			methodIds = new ProfilerMethod[500];
			typeIds = new ProfilerType[100];
			moduleIds = new ProfilerModule[10];
			stackIds = new StackInfo[2000];
			allocIds = new AllocInfo[2000];
			fileIdStackInfoId = new uint[1000];
			stackIdLimit = 1;                // Reserve 0 for an illegal value
			allocIdLimit = 1;                // reserve 0 for an illegal value

			FastStream stream = null;
			try
			{
				stream = new FastStream(filePath);
				ReadFile(stream);
			}
			finally
			{
				if (stream != null)
					stream.BaseStream.Close();
			}
		}

		/// <summary>
		/// Indicates that 'ReadFile' should return immediately.   
		/// </summary>
		public void Abort()
		{
			abort = true;
		}

		// Routines for manipulating ProfilerAllocID
		// As a concession to efficiency, the APIs above don't return allocation objects but rather a
		// ProfilerAllocID to represent an allocation. This ID represents an opaque handle for accessing the
		// Allocation information. Use the routines below to acesss them. 
		/// <summary>
		/// Returns the ID that is one larger than the last valid Alloc ID.   Note that this is valid only AFTER a completed ReadFile() call
		/// </summary>
		/// <returns></returns>
		public ProfilerAllocID AllocIdLimit { get { return (ProfilerAllocID)allocIdLimit; } }
		public ProfilerStackTraceID GetAllocStack(ProfilerAllocID allocId)
		{
			var ret = allocIds[(int)allocId].stackId;
			Debug.Assert(ret < StackIdLimit);
			return ret;
		}
		public ProfilerTypeID GetAllocTypeId(ProfilerAllocID allocId)
		{
			var ret = allocIds[(int)allocId].typeId;
			Debug.Assert(ret != 0);         // 0 is reserved as a sentinal.  
			Debug.Assert(ret < TypeIdLimit);
			return ret;
		}
		public ProfilerType GetAllocType(ProfilerAllocID allocId)
		{
			return typeIds[(int)GetAllocTypeId(allocId)];
		}
		public uint GetAllocSize(ProfilerAllocID allocId)
		{
			return allocIds[(int)allocId].size;
		}

		// Routines for manipulating ProfilerStackTraceID

		// As a concession to efficiency, the APIs above don't return stack objects but rather a
		// ProfilerStackTraceID to represent a stack. This ID represents an opaque handle for accessing the
		// stack trace. Use the routines below to acesss it. Effectively, a stack trace is a linked list of
		// ProfilerMethod structures. The idea is you use 'Method' API to get the ProfielrMethod, and
		// 'NextFrame' API to find the parent. ProfilerStackTraceID.Null terminates the list. see
		// code:#SampleProgram for an example use.
		/// <summary>
		/// Returns the ID that is one larger than the last valid Stack ID.   Note that this is valid only AFTER a completed ReadFile() call
		/// </summary>
		/// <returns></returns>
		public ProfilerStackTraceID StackIdLimit { get { return (ProfilerStackTraceID)stackIdLimit; } }
		public ProfilerStackTraceID NextFrame(ProfilerStackTraceID stackId)
		{
			var ret = stackIds[(int)stackId].stackId;
			Debug.Assert(ret < StackIdLimit);
			return ret;
		}

		public ProfilerMethod Method(ProfilerStackTraceID stackId)
		{
			Debug.Assert(stackId < StackIdLimit);
			return methodIds[(int)stackIds[(int)stackId].methodId];
		}

		/// <summary>
		/// Returns the ID that is one larger than the last valid Method ID, Note that this is valid only AFTER a completed ReadFile() call
		/// </summary>
		public ProfilerMethodID MethodIdLimit { get { return (ProfilerMethodID)methodIdLimit; } }
		public ProfilerMethod GetMethodById(ProfilerMethodID methodId)
		{
			Debug.Assert(methodId < MethodIdLimit);
			return methodIds[(int)methodId];
		}

		/// <summary>
		/// Returns the ID that is one larger than the last valid FieldType ID, Note that this is valid only AFTER a completed ReadFile() call
		/// </summary>
		public ProfilerTypeID TypeIdLimit { get { return (ProfilerTypeID)typeIdLimit; } }
		public ProfilerType GetTypeById(ProfilerTypeID typeId)
		{
			Debug.Assert(typeId < TypeIdLimit);
			return typeIds[(int)typeId];
		}

		/// <summary>
		/// Returns the ID that is one larger than the last valid Module ID, Note that this is valid only AFTER a completed ReadFile() call
		/// </summary>
		public ProfilerModuleID ModuleIdLimit { get { return (ProfilerModuleID)moduleIdLimit; } }
		public ProfilerModule GetModuleById(ProfilerModuleID moduleId)
		{
			Debug.Assert((uint)moduleId < (uint)ModuleIdLimit);
			return moduleIds[(int)moduleId];
		}

		#region MappingStackAndAllocIds

		const uint IsAllocIdMask = 0x80000000;
		const uint IdValueMask = 0x7FFFFFFF;
		private ProfilerStackTraceID GetStackIdForFileId(uint fileStackId)
		{
			ProfilerStackTraceID ret = (ProfilerStackTraceID)fileIdStackInfoId[fileStackId];
			if ((IsAllocIdMask & (uint)ret) != 0)
			{
				ProfilerAllocID allocId = GetAllocIdForFileId(fileStackId);
				ret = allocIds[(int)allocId].stackId;
			}
			Debug.Assert(((uint)ret & IsAllocIdMask) == 0);
			Debug.Assert(ret != 0 || fileStackId <= 1);
			// TODO Debug.Assert(fileStackId <= 1 || methodIds[(int)stackIds[(int)ret].methodId].name != null);
			return ret;
		}
		private ProfilerAllocID GetAllocIdForFileId(uint fileStackId)
		{
			uint ret = fileIdStackInfoId[fileStackId];
			Debug.Assert((ret & IsAllocIdMask) != 0);
			ret = ret & IdValueMask;
			Debug.Assert(fileStackId == 0 || ret != 0);
			Debug.Assert((ProfilerAllocID)ret < AllocIdLimit);
			Debug.Assert(ret == 0 || typeIds[(int)allocIds[ret].typeId].name != null);
			return (ProfilerAllocID)ret;
		}
		private static uint SetStackId(ProfilerStackTraceID stackId)
		{
			return (uint)stackId;
		}

		private static uint SetAllocId(uint allocId)
		{
			return (uint)allocId | IsAllocIdMask;
		}

		#endregion

		#region PrivateMethods
		[Conditional("DEBUG")]
		private void VerboseDebug(string message)
		{
			// Debug.WriteLine(message);
		}

		private List<ProfilerTypeID> GetTypesNeedingModule(string typeName, bool createIfAbsent = true)
		{
			if (typesNeedingModule == null)
			{
				typesNeedingModule = new Dictionary<string, List<ProfilerTypeID>>();
				for (int i = 1; i < (int)typeIdLimit; i++)  // zero is a illegal type ID 
				{
					var type = typeIds[i];
					AddTypeToTypesNeedingModule((ProfilerTypeID)i, type.name);
				}
			}
			var typeKey = GetKey(typeName);
			List<ProfilerTypeID> ret;
			if (typesNeedingModule.TryGetValue(typeKey, out ret))
				return ret;

			if (!createIfAbsent)
				return null;

			CreateType(TypeIdLimit, typeName, false);
			return GetTypesNeedingModule(typeName);     // This time it will succeed.  
		}
		private ProfilerModuleID GetModuleIdForName(string moduleName)
		{
			if (moduleNameToId == null)
			{
				moduleNameToId = new Dictionary<string, int>();
				for (int i = 0; i < (int)moduleIdLimit; i++)
				{
					var module = moduleIds[i];
					moduleNameToId[module.name] = i;
				}
			}
			int id;
			if (moduleNameToId.TryGetValue(moduleName, out id))
				return (ProfilerModuleID)id;

			var newModuleId = ModuleIdLimit;
			CreateModule(newModuleId, moduleName, 0, ProfilerStackTraceID.Null);
			return newModuleId;
		}

		#endregion

		#region PrivateState
		struct StackInfo
		{
			internal void Set(ProfilerMethodID methodId, ProfilerStackTraceID stackId)
			{
				Debug.Assert((uint)methodId != BadValue && (uint)stackId != BadValue);
				this.methodId = methodId;
				this.stackId = stackId;
			}
			internal ProfilerMethodID methodId;
			internal ProfilerStackTraceID stackId;
		}

		struct AllocInfo
		{
			internal void Set(ProfilerTypeID typeId, uint size, ProfilerStackTraceID stackId)
			{
				Debug.Assert((uint)typeId != BadValue && (uint)stackId != BadValue);
				this.typeId = typeId;
				this.size = size;
				this.stackId = stackId;
			}
			internal ProfilerTypeID typeId;
			internal uint size;
			internal ProfilerStackTraceID stackId;
		};

		/// <summary>
		/// Used to abort the run early
		/// </summary>
		bool abort;

		ProfilerMethod[] methodIds;     // As many as there are distinct methods
		uint methodIdLimit;
		ProfilerType[] typeIds;         // As many as there are distinct types     
		uint typeIdLimit;
		internal ProfilerModule[] moduleIds;     // As many as there are distinct modules
		uint moduleIdLimit;

		StackInfo[] stackIds;           // As many as there are distinct stacks (and parts of stacks)
		uint stackIdLimit;
		AllocInfo[] allocIds;           // As many as there are distinct nodeId/stack combinations
		uint allocIdLimit;

		Dictionary<string, int> moduleNameToId;                     // only needed during reading. 
		Dictionary<string, List<ProfilerTypeID>> typesNeedingModule;      // only needed during reading. 

		/// <summary>
		/// the CLRProfiler log file has a rather painful encoding of stacks that is hard to design an
		/// efficient and intuitive access API around because the ID for the 'n' record represents a
		/// chunk of the stack, not just one frame (and try to handle both having allocation nodeId information
		/// sometimes and sometimes not).  We remove this complexity during reading.  The entries in the
		/// StackInfo table represent exactly one frame, and the enties int the AllocInfo table represent
		/// exactly one allocation nodeId/Size and its stack.  
		/// 
		/// However during reading, we need to convert from the ID used in the file, to the one used
		/// internally.  The table below remembers this mapping (from ID used in the file, to ID used
		/// in either StackInfo or AllocInfo.
		/// 
		/// If the upper bit is reset (entry is postive) then the Id is for the StackInfo table, 
		/// otherwise mask off 0x80000000 and the entry is for the AllocInfo table
		/// </summary>
		uint[] fileIdStackInfoId;
		internal const uint BadValue = unchecked((uint)(-1));
		internal const Address BadAddress = unchecked((Address)(-1L));

		internal ProfilerStack[] Frames;         // only used when using ProfilerFrame (fat mechanism)
		#endregion
	}

	/// <summary>
	/// Represents one stack trace. It represents a tuple that has a 'NextFrame' field and a 'Method' field,
	/// but does so more efficiently than could be done if it were an object. See the
	/// code:ClrProfilerCallBacks.NextFrame and code:ClrProfilerCallBacks.Method for more on usage.
	/// </summary>
	public enum ProfilerStackTraceID
	{
		Null = 0,
	};

	public enum ProfilerModuleID { Invalid = -1 }

	public enum ProfilerTypeID { Invalid = -1 }

	public enum ProfilerMethodID { }

	/// <summary>
	/// Represents one allocation 
	/// </summary>
	public enum ProfilerAllocID
	{
		Null = 0,
	};

	/// <summary>
	/// Represents a single method in the target application 
	/// </summary>
	public sealed class ProfilerMethod
	{
		internal ProfilerMethod(ProfilerMethodID methodId, string name, string sig, Address address, uint size, ProfilerModule module, ProfilerStackTraceID firstStackId)
		{
			Debug.Assert(name != null && sig != null && (uint)address != ClrProfilerParser.BadValue && size != ClrProfilerParser.BadValue && module != null && (uint)firstStackId != ClrProfilerParser.BadValue);
			this.MethodId = methodId;
			this.name = name;
			this.signature = sig;
			this.address = address;
			this.size = size;
			this.module = module;
			this.firstStackId = firstStackId;
		}

		public string FullName
		{
			get
			{
				int paren = signature.IndexOf('(');
				if (paren < 0)
					return name;
				return name + signature.Substring(paren);
			}
		}

		public override string ToString()
		{
			return "ProfilerMethod " + name;
		}
		public string name;
		public string signature;
		public Address address;
		public uint size;
		public ProfilerModule module;
		public ProfilerStackTraceID firstStackId;
		public object UserData;
		public ProfilerMethodID MethodId;
	};

	/// <summary>
	/// Represents a nodeId allocated by the target application
	/// </summary>
	public sealed class ProfilerType
	{
		public override string ToString()
		{
			return "ProfilerType " + name;
		}
		public string name;
		public bool isFinalizable;
		public object UserData;
		public ProfilerTypeID TypeId;
		public ProfilerModuleID ModuleId;       // May be Invalid
		public ProfilerModule Module
		{
			get
			{
				if ((uint)ModuleId < (uint)m_parser.moduleIds.Length)
					return m_parser.GetModuleById(ModuleId);
				return null;
			}
		}         // May be null 
		public string ModuleName
		{
			get
			{
				var module = Module;
				if (module == null)
					return string.Empty;
				else
					return module.name;
			}
		}

		#region private
		internal ProfilerType(ClrProfilerParser parser, ProfilerTypeID typeId, string name, bool isFinalizable)
		{
			this.m_parser = parser;
			Debug.Assert(name != null);
			this.TypeId = typeId;
			this.name = FixGenerics(name);
			this.isFinalizable = isFinalizable;
			this.ModuleId = ProfilerModuleID.Invalid;
		}
		/// <summary>
		/// The format for generics in the file is not what you see in source code.  Fix this....
		/// </summary>
		internal static string FixGenerics(string name)
		{
			name = name.Replace("+", ".");      // + used for nested classes.   

			// Quick check to see if there are generics at all. 
			if (name.IndexOf('`') < 0)
				return name;

			// morph Dictionary`2[[T1,M1],[T2,M2]] => Dictionary<T1,T2>

			// First change [] to {} just to avoid ambiguitites
			name = Regex.Replace(name, @"\[(,*)\]", "{$1}");
			for (;;)
			{
				bool morphed = false;
				// Morph [T,M] => T    until it can't be matched.  
				var newName = Regex.Replace(name, @"([^\d\w])\[([^[\]]+),[^,\]]+\]", "$1$2");
				if (name != newName)
					morphed = true;
				name = newName;

				// Morph Dictionary`2+Entry[...] => Dictionary`2+Entry<...> 
				newName = Regex.Replace(name, @"([\d\w])\[([^[\]]+)\]", "$1<$2>");
				if (name != newName)
					morphed = true;
				name = newName;
				if (!morphed || name.IndexOf('[') < 0)
					break;
			}
			// Undo the {}
			name = Regex.Replace(name, @"{(,*)}", "[$1]");

			// remove  `2
			name = Regex.Replace(name, @"`\d*", "");
			return name;
		}

		ClrProfilerParser m_parser;
		#endregion
	}

	/// <summary>
	/// Represents a module (DLL file), loaded by the target application
	/// </summary>
	public sealed class ProfilerModule
	{
		public ProfilerModule(ProfilerModuleID moduleId, string name, Address address, ProfilerStackTraceID firstStackId)
		{
			Debug.Assert(name != null && (uint)address != ClrProfilerParser.BadValue && (uint)firstStackId != ClrProfilerParser.BadValue);
			this.name = name;
			this.address = address;
			this.firstStackId = firstStackId;
			this.ModuleId = moduleId;
		}
		public override string ToString()
		{
			return "ProfilerModule " + name;
		}

		/// <summary>
		/// This is the full path name of the module
		/// </summary>
		public string name;
		public Address address;
		public ProfilerStackTraceID firstStackId;
		public object UserData;
		public ProfilerModuleID ModuleId;
	}

	/// <summary>
	/// If you need a real object to represent the stack frame, 
	/// </summary>
	public sealed class ProfilerStack : StackFrame
	{
		public ProfilerStack(ClrProfilerParser parser, ProfilerStackTraceID stackId)
		{
			this.Parser = parser;
			this.StackId = stackId;
		}
		public override StackFrame Caller
		{
			get { return GetFrame(Parser.NextFrame(StackId)); }
		}

		private StackFrame GetFrame(ProfilerStackTraceID stackId)
		{
			if (stackId == ProfilerStackTraceID.Null)
				return null;

			ProfilerStack[] frames = Parser.Frames;
			Parser.Frames = frames = ArrayUtilities<ProfilerStack>.InsureCapacity(frames, (uint)stackId);
			ClrProfiler.StackFrame ret = frames[(int)stackId];
			if (ret == null)
				ret = frames[(int)stackId] = new ProfilerStack(Parser, stackId);
			return ret;
		}
		public override string Name
		{
			get
			{
				if (name == null)
				{
					ProfilerMethod method = Parser.Method(StackId);
					int paren = method.signature.IndexOf('(');
					if (paren < 0)
						paren = 0;
					name = method.name + method.signature.Substring(paren);
				}
				return name;
			}
		}
		public override string ModulePath
		{
			get { return Parser.Method(StackId).module.name; }
		}
		public int Depth
		{
			get
			{
				var ret = 0;
				var stackId = StackId;
				while (stackId != ProfilerStackTraceID.Null)
				{
					stackId = Parser.NextFrame(stackId);
					ret++;
				}
				return ret;
			}
		}

		public override string ToString()
		{
			var sb = new StringBuilder();
			sb.AppendLine("<ClrProfilerStack>");
			var stackId = StackId;
			while (stackId != ProfilerStackTraceID.Null)
			{
				var method = Parser.Method(stackId);
				sb.AppendFormat("<ClrProfilerFrame StackId=\"{0}\" MethodId=\"{1}\" Name=\"{2}\"/>\r\n",
					stackId, method.MethodId, method.FullName);
				stackId = Parser.NextFrame(stackId);
			}
			sb.AppendLine("</ClrProfilerStack>");
			return sb.ToString();
		}

		public ProfilerMethod Method { get { return Parser.Method(StackId); } }
		public ClrProfilerParser Parser;
		public ProfilerStackTraceID StackId;
		public object UserData;


		private string name;
	}

	/// <summary>
	/// The GC Memory is allocated in relatively large contiguous regions called 'segments' ProfilerGCSegment
	/// represents one such segment. (Only used in code:ClrProfilerCallBacks.GCStart and EndGC)
	/// </summary>
	public sealed class ProfilerGCSegment
	{
		internal ProfilerGCSegment(Address rangeStart, uint rangeLength, uint rangeLengthReserved, int rangeGeneration)
		{
			this.rangeStart = rangeStart;
			this.rangeLength = rangeLength;
			this.rangeLengthReserved = rangeLengthReserved;
			this.rangeGeneration = rangeGeneration;
		}
		public Address rangeStart;
		public uint rangeLength;
		public uint rangeLengthReserved;
		public int rangeGeneration;

		public override string ToString()
		{
			return String.Format("0x{0:x}-0x{1:x}", rangeStart, rangeStart + rangeLengthReserved);
		}
	}

	/// <summary>
	/// Indiciates where the GC root came from (Only used in code:ClrProfilerCallBacks.OnGCRoot)
	/// </summary>
	public enum GcRootKind
	{
		Other = 0x0,
		Stack = 0x1,
		Finalizer = 0x2,
		Handle = 0x3,
	};

	/// <summary>
	/// Indiciates  any special properties the GC root can have (Only used in code:ClrProfilerCallBacks.OnGCRoot)
	/// </summary>
	public enum GcRootFlags
	{
		Pinning = 0x1,
		WeakRef = 0x2,
		Interior = 0x4,
		Refcounted = 0x8,
	};

	// #SampleProgram : The program has been #if'ed out but turning it on yields a working program.  
#if false
    public class Program
    {
        static void Main(string[] args)
        {
            Program p = new Program(args[0]);
        }
        public Program(string profilerFile)
        {
            profiler = new ClrProfilerParser();
            profiler.Allocation += OnAllocation;
            profiler.Call += OnCall;
            profiler.ReadFile(profilerFile);
        }
        private void OnAllocation(ProfilerType type, uint size, ProfilerStackTraceID stackId, Address objectAddress, uint threadId)
        {
            Console.WriteLine("Alloc Event on thread " + threadId + " type = " + type.name + " size = " + size);
            PrintStack(stackId);
        }
        private void OnCall(ProfilerStackTraceID stackId, uint threadId)
        {
            Console.WriteLine("Call Event on thread " + threadId);
            PrintStack(stackId);
        }
        private void PrintStack(ProfilerStackTraceID stackId)
        {
            while (stackId != ProfilerStackTraceID.Null)
            {
                Console.WriteLine("    " + profiler.Method(stackId).name);
                stackId = profiler.NextFrame(stackId);
            }
        }
        private ClrProfilerParser profiler;
    }
#endif
}

