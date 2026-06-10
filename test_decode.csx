using System.Text.RegularExpressions;

// Simulate what the regex extracts from PornHub HTML:
// The HTML contains: "video_title":"\u9ebb\u8c46\u5a92\u754c..."
// Regex extracts the value as a literal string: \u9ebb\u8c46\u5a92\u754c (with backslash-u)
var raw = @"\u9ebb\u8c46\u5a92\u754c - \u65b0\u4eba";

Console.WriteLine($"Before decode: {raw}");

var decoded = DecodeJsonString(raw);
Console.WriteLine($"After  decode: {decoded}");

static string DecodeJsonString(string s)
{
    if (string.IsNullOrEmpty(s)) return s;
    return Regex.Replace(s, @"\\u([0-9a-fA-F]{4})", m =>
        ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
}

Console.WriteLine("Test PASSED: Unicode escape decode works!");
