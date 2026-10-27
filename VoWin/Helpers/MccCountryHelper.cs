using System.Collections.Concurrent;

namespace VoWin.Helpers
{
    public record CountryMeta(string Code, string Name, string Flag, string[] Mccs)
    {
        public string DisplayTitle => $"{Flag} {Name} ({Code})";
        public string MccSummary => $"MCC: {string.Join(", ", Mccs)}";
        public string SearchKey => $"{Name} {Code} {Flag}".ToLowerInvariant();
    }

    /// <summary>
    /// Resolves an MCC to a country/region. The MCC→country table is generated
    /// from the ITU E.212 allocation (see tools/gen_card_tables.py) and covers
    /// every assigned MCC; unknown codes fall back to a raw "MCC xxx" label.
    /// </summary>
    public static class MccCountryHelper
    {
        private static readonly ConcurrentDictionary<string, CountryMeta> MccToCountry = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, CountryMeta> CodeToCountry = new(StringComparer.OrdinalIgnoreCase);

        static MccCountryHelper()
        {
            foreach (var (mcc, countryName) in CardLookupTables.MccCountry)
            {
                var iso = CardLookupTables.MccCountryIso.TryGetValue(mcc, out var value) ? value : string.Empty;
                var meta = new CountryMeta(
                    iso.ToUpperInvariant(),
                    countryName,
                    string.Empty,
                    new[] { mcc });

                CodeToCountry[meta.Code] = meta;
                MccToCountry[mcc] = meta;
            }
        }

        public static IReadOnlyList<CountryMeta> GetAllKnownCountries() =>
            CodeToCountry.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

        public static CountryMeta? FindByMcc(string? mcc)
        {
            if (string.IsNullOrWhiteSpace(mcc)) return null;
            var clean = mcc.Trim();
            if (MccToCountry.TryGetValue(clean, out var meta)) return meta;
            // Some readers report a PLMN ("460-00") instead of a bare MCC.
            var dash = clean.IndexOf('-');
            if (dash > 0) clean = clean[..dash];
            if (clean.Length >= 3 && MccToCountry.TryGetValue(clean[..3], out var meta3)) return meta3;
            return null;
        }

        public static CountryMeta? FindByCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            return CodeToCountry.TryGetValue(code.Trim(), out var meta) ? meta : null;
        }

        /// <summary>
        /// Country name for an MCC, or an empty string when the MCC is unknown.
        /// </summary>
        public static string GetCountryName(string? mcc) => FindByMcc(mcc)?.Name ?? string.Empty;
    }
}
