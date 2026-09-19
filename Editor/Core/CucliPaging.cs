using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace CustomUnityCli.Editor
{
    /// <summary>One page of results plus the metadata a caller needs to fetch the next one.</summary>
    public sealed class CucliPage<T>
    {
        public List<T> Items = new List<T>();
        public int Total;
        public int Offset;
        public int PageSize;
        public int PageNumber;
        public bool HasMore;
        public string NextCursor;
    }

    /// <summary>
    /// Cursor and page-number paging shared by every list-returning command.
    ///
    /// Output size is the main way a CLI like this becomes unusable, so every list is paged by
    /// default and the envelope always states the true total. The cursor is an opaque, offset-based
    /// token: listings are produced in a single Editor job, so they do not need to stay valid across
    /// unrelated edits.
    /// </summary>
    public static class CucliPaging
    {
        public const int DefaultPageSize = 100;
        public const int MaxPageSize = 500;

        public static int ResolvePageSize(CucliArgs args, int fallback = DefaultPageSize)
        {
            int pageSize = args.GetInt("pageSize", args.GetInt("page_size", 0));
            if (pageSize <= 0) pageSize = args.GetInt("limit", fallback);
            if (pageSize <= 0) return fallback;
            return Math.Min(pageSize, MaxPageSize);
        }

        public static CucliPage<T> Slice<T>(IReadOnlyList<T> source, CucliArgs args, int fallbackPageSize = DefaultPageSize)
        {
            var page = new CucliPage<T>();
            if (source == null) return page;

            page.Total = source.Count;
            page.PageSize = ResolvePageSize(args, fallbackPageSize);

            int offset = 0;
            string cursor = args.GetString("cursor");
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                int decoded;
                if (TryDecodeCursor(cursor, out decoded)) offset = decoded;
            }
            else
            {
                int pageNumber = args.GetInt("pageNumber", args.GetInt("page_number", 0));
                if (pageNumber > 0)
                {
                    offset = (pageNumber - 1) * page.PageSize;
                }
            }

            if (offset < 0) offset = 0;
            if (offset > page.Total) offset = page.Total;

            page.Offset = offset;
            page.PageNumber = page.PageSize <= 0 ? 1 : (offset / page.PageSize) + 1;

            int end = Math.Min(offset + page.PageSize, page.Total);
            for (int index = offset; index < end; index++) page.Items.Add(source[index]);

            page.HasMore = end < page.Total;
            if (page.HasMore) page.NextCursor = EncodeCursor(end);
            return page;
        }

        /// <summary>Standard paging envelope; merge extra fields into <paramref name="payload"/>.</summary>
        public static JObject Envelope<T>(CucliPage<T> page, JArray items, JObject payload = null)
        {
            var result = payload ?? new JObject();
            result["items"] = items;
            result["total"] = page.Total;
            result["returned"] = page.Items.Count;
            result["offset"] = page.Offset;
            result["pageSize"] = page.PageSize;
            result["pageNumber"] = page.PageNumber;
            result["hasMore"] = page.HasMore;
            if (page.HasMore && page.NextCursor != null) result["nextCursor"] = page.NextCursor;
            return result;
        }

        public static string EncodeCursor(int offset)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes("cucli:" + offset.ToString(CultureInfo.InvariantCulture)));
        }

        public static bool TryDecodeCursor(string cursor, out int offset)
        {
            offset = 0;
            if (string.IsNullOrWhiteSpace(cursor)) return false;
            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Trim()));
                if (!decoded.StartsWith("cucli:", StringComparison.Ordinal)) return false;
                return int.TryParse(
                    decoded.Substring("cucli:".Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out offset);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
