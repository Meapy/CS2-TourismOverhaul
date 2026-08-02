using System.Collections.Generic;
using Colossal;

namespace TourismOverhaul
{
    /// <summary>
    /// A translated locale that falls back to English for anything it does not cover.
    ///
    /// The mod's settings descriptions are several paragraphs each and full of domain terms —
    /// surge pricing, outside connections, attractiveness. Labels translate cleanly; those
    /// descriptions do not, and a confidently wrong description is worse than an English one
    /// because the player cannot tell it is wrong.
    ///
    /// So each locale supplies the short strings it is sure of, and this class fills the rest from
    /// LocaleEN. A player sees their own language everywhere the interface names something, and
    /// English only where the mod is explaining itself at length.
    ///
    /// Adding a translation is therefore additive and safe: contribute a few more keys and they
    /// take effect; miss one and it stays readable rather than blank.
    /// </summary>
    public sealed class LocaleOverlay : IDictionarySource
    {
        private readonly LocaleEN m_Fallback;
        private readonly IReadOnlyDictionary<string, string> m_Translations;

        public LocaleOverlay(TourismOverhaulSetting setting, IReadOnlyDictionary<string, string> translations)
        {
            m_Fallback = new LocaleEN(setting);
            m_Translations = translations;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            Dictionary<string, string> entries = new Dictionary<string, string>();

            foreach (KeyValuePair<string, string> entry in m_Fallback.ReadEntries(errors, indexCounts))
            {
                entries[entry.Key] = entry.Value;
            }

            // Applied second, so a translation always wins over the English baseline.
            foreach (KeyValuePair<string, string> entry in m_Translations)
            {
                entries[entry.Key] = entry.Value;
            }

            return entries;
        }

        public void Unload()
        {
        }
    }
}
