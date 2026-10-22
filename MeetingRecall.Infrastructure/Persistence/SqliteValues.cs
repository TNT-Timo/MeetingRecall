using System.Globalization;

namespace MeetingRecall.Infrastructure.Persistence;

internal static class SqliteValues
{
    public static string FromGuid(Guid value) => value.ToString("D");

    public static Guid ToGuid(string value) => Guid.ParseExact(value, "D");

    public static string FromDateTimeOffset(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ToDateTimeOffset(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);

    public static object FromNullableDateTimeOffset(DateTimeOffset? value) =>
        value is null ? DBNull.Value : FromDateTimeOffset(value.Value);
}
