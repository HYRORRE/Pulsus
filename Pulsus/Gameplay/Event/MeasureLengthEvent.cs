﻿using System.Diagnostics;

namespace Pulsus.Gameplay
{
	[DebuggerDisplay("{ToString()}")]
	public class MeasureLengthEvent : Event
	{
		public double measureLength;

		public MeasureLengthEvent(long pulse, double measureLength = 1.0)
			: base(pulse)
		{
			this.measureLength = measureLength;
		}
	}
}
