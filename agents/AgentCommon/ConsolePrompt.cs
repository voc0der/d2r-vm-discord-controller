namespace AgentCommon;

public static class ConsolePrompt
{
    public static bool CanPrompt => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static string ReadString(string label, string? defaultValue = null, bool allowEmpty = true)
    {
        while (true)
        {
            Console.Write(defaultValue is null
                ? $"{label}: "
                : $"{label} [{defaultValue}]: ");

            var value = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(value))
            {
                if (!string.IsNullOrWhiteSpace(defaultValue))
                {
                    return defaultValue;
                }

                if (allowEmpty)
                {
                    return "";
                }

                Console.WriteLine("Value is required.");
                continue;
            }

            return value.Trim();
        }
    }

    public static int ReadInt(string label, int defaultValue, int minValue = 0, int maxValue = int.MaxValue)
    {
        while (true)
        {
            var value = ReadString(label, defaultValue.ToString(), allowEmpty: false);
            if (int.TryParse(value, out var parsed) && parsed >= minValue && parsed <= maxValue)
            {
                return parsed;
            }

            Console.WriteLine($"Enter a number between {minValue} and {maxValue}.");
        }
    }

    public static ulong? ReadOptionalUlong(string label, ulong? defaultValue = null)
    {
        while (true)
        {
            var value = ReadString(label, defaultValue?.ToString(), allowEmpty: true);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (ulong.TryParse(value, out var parsed))
            {
                return parsed;
            }

            Console.WriteLine("Enter a numeric ID or leave it blank.");
        }
    }

    public static bool ReadBool(string label, bool defaultValue)
    {
        var suffix = defaultValue ? "[Y/n]" : "[y/N]";
        while (true)
        {
            Console.Write($"{label} {suffix}: ");
            var value = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            value = value.Trim();
            if (value.Equals("y", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Equals("n", StringComparison.OrdinalIgnoreCase)
                || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                || value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Console.WriteLine("Enter y or n.");
        }
    }

    public static string[] ReadCsv(string label, string? defaultValue = null)
    {
        var value = ReadString(label, defaultValue, allowEmpty: true);
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
