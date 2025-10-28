using FoodMenu_Extract.Models;
using Microsoft.Data.SqlClient;
using System.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using FoodMenu_Extract.Models;
using Microsoft.Data.SqlClient;

namespace FoodMenu_Extract.Services
{
    public class DataRepository
    {
        private readonly string _connectionString;
        private readonly int _batchSize;

        public DataRepository(string connectionString, int batchSize = 100)
        {
            _connectionString = connectionString;
            _batchSize = batchSize;
        }

        // Stream hotels; optional limit via maxCount (use int.MaxValue for no limit)
        public async IAsyncEnumerable<HotelDto> ReadHotelsAsync(int maxCount = int.MaxValue)
        {
            bool useTop = maxCount > 0 && maxCount < int.MaxValue;
            var sql = useTop
                ? "SELECT TOP(@Top) productID, hotelName, address FROM HotelStagingRecords"
                : "SELECT productID, hotelName, address FROM HotelStagingRecords";

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };

            if (useTop)
            {
                cmd.Parameters.AddWithValue("@Top", maxCount);
            }

            using var rdr = await cmd.ExecuteReaderAsync(CommandBehavior.Default);

            while (await rdr.ReadAsync())
            {
                var dto = new HotelDto
                {
                    ProductId = rdr["productID"]?.ToString() ?? string.Empty,
                    HotelName = rdr["hotelName"]?.ToString() ?? string.Empty,
                    Address = rdr["address"]?.ToString() ?? string.Empty
                };
                yield return dto;
            }
        }

        // Upsert into HotelMenus table. Adjust column names to match your schema.
        public async Task UpsertMenuResultAsync(MenuResultDto result)
        {
            const string sql = @"
                   MERGE INTO HotelMenus AS target
                   USING (VALUES (@HotelProductID, @SearchQuery)) AS source(HotelProductID, SearchQuery)
                     ON ISNULL(target.HotelProductID,'') = ISNULL(source.HotelProductID,'') AND ISNULL(target.SearchQuery,'') = ISNULL(source.SearchQuery,'')
                   WHEN MATCHED THEN
                   UPDATE SET
                         HasMenu = @HasMenu,
                         MenuSourceUrl = @MenuSourceUrl,
                         MenuText = @MenuText,
                         MenuJson = @MenuJson,
                         SearchDate = SYSUTCDATETIME()
                         WHEN NOT MATCHED THEN
                   INSERT (HotelProductID, SearchQuery, HasMenu, MenuSourceUrl, MenuText, SearchDate, MenuJson)
                   VALUES (@HotelProductID, @SearchQuery, @HasMenu, @MenuSourceUrl, @MenuText, SYSUTCDATETIME(), @MenuJson);";


            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            var searchQuery = (result.HotelName ?? string.Empty).Trim();

            cmd.Parameters.AddWithValue("@HotelProductID", (object?)result.HotelProductId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SearchQuery", string.IsNullOrEmpty(searchQuery) ? (object)DBNull.Value : searchQuery);
            cmd.Parameters.AddWithValue("@HasMenu", result.HasMenu);
            cmd.Parameters.AddWithValue("@MenuSourceUrl", (object?)result.MenuSourceUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MenuText", (object?)result.MenuText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MenuJson", (object?)result.MenuJson ?? DBNull.Value);

            cmd.CommandType = CommandType.Text;
            await cmd.ExecuteNonQueryAsync();
        }
    }
}