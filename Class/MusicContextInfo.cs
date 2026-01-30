using StardewValley;
using System.Reflection;

namespace DMMusic
{
    internal readonly struct MusicContextInfo
    {
        public readonly string Location;
        public readonly bool EventUp;
        public readonly string? EventId;

        public MusicContextInfo(string location, bool eventUp, string? eventId)
        {
            Location = location;
            EventUp = eventUp;
            EventId = eventId;
        }

        public static MusicContextInfo Get()
        {
            string loc = Game1.player?.currentLocation?.NameOrUniqueName ?? "UnknownLocation";

            bool eventUp = Game1.eventUp && Game1.CurrentEvent != null;
            string? eventId = null;

            if (eventUp && Game1.CurrentEvent != null)
            {
                object ev = Game1.CurrentEvent;
                eventId = TryGetMemberAsString(ev, "id")
                       ?? TryGetMemberAsString(ev, "Id")
                       ?? TryGetMemberAsString(ev, "eventId");

                if (string.IsNullOrWhiteSpace(eventId))
                    eventId = null;
            }

            return new MusicContextInfo(loc, eventUp, eventId);
        }

        public string ToDebugString()
        {
            if (!EventUp)
                return $"EventUp=false, Location={Location}";
            return $"EventUp=true, Location={Location}, EventId={EventId ?? "UnknownEventId"}";
        }

        private static string? TryGetMemberAsString(object instance, string name)
        {
            var t = instance.GetType();

            var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null)
            {
                var val = prop.GetValue(instance);
                if (val != null) return val.ToString();
            }

            var field = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                var val = field.GetValue(instance);
                if (val != null) return val.ToString();
            }

            return null;
        }
    }
}
