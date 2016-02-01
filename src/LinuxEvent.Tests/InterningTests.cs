﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Diagnostics.Tracing.StackSources;
using Xunit;

namespace LinuxTracing.Tests
{
	public class InterningTests
	{
		private void InterningStackCountTest(string source, int expectedStackCount)
		{
			LinuxPerfScriptEventParser parser = new LinuxPerfScriptEventParser(source);
			parser.Parse(testing: true);

			Assert.Equal(expectedStackCount, 0);
		}

		[Fact]
		public void OneSample()
		{
			string path = Constants.GetPerfDumpPath("onegeneric");
			this.InterningStackCountTest(path, expectedStackCount: 3);
		}

		[Fact]
		public void TwoSameSamples()
		{
			string path = Constants.GetPerfDumpPath("twogenericsame");
			this.InterningStackCountTest(path, expectedStackCount: 3);
		}

		[Fact]
		public void TwoSameLongSamples()
		{
			string path = Constants.GetPerfDumpPath("twogenericsamelongstacks");
			this.InterningStackCountTest(path, expectedStackCount: 8);
		}

		[Fact]
		public void TwoAlteredLongSamples()
		{
			string path = Constants.GetPerfDumpPath("twodifferentlongstacks");
			this.InterningStackCountTest(path, expectedStackCount: 10);
		}
	}
}
