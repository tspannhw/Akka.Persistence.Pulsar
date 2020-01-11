﻿using System;

namespace Pulsar_Sample.Event
{
    public class ReadSystemCurrentTimeUtc : IEvent
    {
        public long EventTime => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public readonly long CurrentTime;
        public ReadSystemCurrentTimeUtc(long time)
        {
            CurrentTime = time;
        }
    }
}