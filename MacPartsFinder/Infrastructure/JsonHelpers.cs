using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MacPartsFinder;

/// <summary>
/// Forgiving, case-insensitive accessors for JSON (Newtonsoft JToken) so supplier parsers
/// don't blow up when a field is missing or null. All of them accept null.
/// </summary>
public static class JsonHelpers
{
    /// <summary>
    /// Parses JSON text. Dates are left as plain text and numbers read as decimal,
    /// so prices like 0.083 come through exactly.
    /// </summary>
    public static JToken ParseJson(string text)
    {
        using var reader = new JsonTextReader(new StringReader(text))
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Decimal
        };
        return JToken.ReadFrom(reader);
    }

    public static JToken? GetPath(this JToken? e, params string[] path)
    {
        var cur = e;
        foreach (var name in path)
        {
            if (cur is not JObject obj) return null;
            var prop = obj.Property(name, StringComparison.OrdinalIgnoreCase);
            if (prop == null) return null;
            cur = prop.Value;
        }
        return cur;
    }

    public static string GetStr(this JToken? e, params string[] path)
    {
        var v = e.GetPath(path);
        if (v == null) return "";
        switch (v.Type)
        {
            case JTokenType.String:
                return ((string?)((JValue)v).Value ?? "").Trim();
            case JTokenType.Integer:
            case JTokenType.Float:
            case JTokenType.Boolean:
                return v.ToString(Formatting.None); // as written in the JSON, e.g. 123, 0.5, true
            default:
                return "";
        }
    }

    public static decimal? GetNum(this JToken? e, params string[] path)
    {
        var v = e.GetPath(path);
        if (v == null) return null;
        switch (v.Type)
        {
            case JTokenType.Integer:
            case JTokenType.Float:
                try
                {
                    return Convert.ToDecimal(((JValue)v).Value, CultureInfo.InvariantCulture);
                }
                catch (OverflowException)
                {
                    return null;
                }
            case JTokenType.String:
                return ParseHelpers.ParseDecimalLoose((string?)((JValue)v).Value);
            default:
                return null;
        }
    }

    public static List<JToken> GetArr(this JToken? e, params string[] path)
    {
        if (e.GetPath(path) is not JArray arr) return new List<JToken>();
        return arr.ToList();
    }
}
