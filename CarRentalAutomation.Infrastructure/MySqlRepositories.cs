using CarRentalAutomation.Core;
using MySqlConnector;

namespace CarRentalAutomation.Infrastructure;

public sealed class MySqlDatabase(string connectionString) : IDatabaseConnectionCheck
{
    public MySqlConnection CreateConnection() => new(connectionString);

    public async Task<(bool Success, string Message)> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("SELECT DATABASE(), VERSION()", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return (true, $"MySQL bağlantısı başarılı. Veritabanı: {reader.GetString(0)}, sunucu: {reader.GetString(1)}");
        }
        catch (MySqlException ex)
        {
            return (false, $"MySQL bağlantısı kurulamadı (kod {ex.Number}): {ex.Message}");
        }
    }
}

public sealed class MySqlSiteCredentialRepository(MySqlDatabase database) : ISiteCredentialRepository
{
    public async Task<string?> GetPhoneNumberAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = "SELECT PhoneNumber FROM SiteCredentials WHERE UserId = @userId ORDER BY UpdatedAt DESC LIMIT 1";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@userId", userId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToString(result);
    }
}

public sealed class MySqlCollectionRepository(MySqlDatabase database, long userId) : ICollectionRepository
{
    private readonly SemaphoreSlim _filterSchemaLock = new(1, 1);
    private volatile bool _filterSchemaReady;

    public async Task<IReadOnlyList<SavedCollection>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<SavedCollection>();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureFilterColumnsAsync(connection, cancellationToken);
        const string sql = "SELECT Id, CollectionName, PickupLocation, PickupDate, ReturnDate, FuelFilter, TransmissionFilter, CreatedAt FROM Collections WHERE UserId=@userId ORDER BY CreatedAt DESC";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SavedCollection(reader.GetInt64("Id"), reader.GetString("CollectionName"),
                new SearchCriteria(reader.GetString("PickupLocation"), reader.GetDateTime("PickupDate"), reader.GetDateTime("ReturnDate"))
                {
                    FuelFilter = reader.GetString("FuelFilter"),
                    TransmissionFilter = reader.GetString("TransmissionFilter")
                },
                reader.GetDateTime("CreatedAt"), []));
        }
        return result;
    }

    public async Task<SavedCollection> SaveAsync(string name, SearchCriteria criteria, IReadOnlyList<CarOffer> cars, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Koleksiyon adı zorunludur.");
        criteria.Validate();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureFilterColumnsAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string insertCollection = "INSERT INTO Collections (UserId, CollectionName, PickupLocation, PickupDate, ReturnDate, FuelFilter, TransmissionFilter) VALUES (@userId,@name,@location,@pickup,@return,@fuelFilter,@transmissionFilter); SELECT LAST_INSERT_ID();";
            await using var command = new MySqlCommand(insertCollection, connection, transaction);
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@name", name.Trim());
            command.Parameters.AddWithValue("@location", criteria.PickupLocation);
            command.Parameters.AddWithValue("@pickup", criteria.PickupAt);
            command.Parameters.AddWithValue("@return", criteria.ReturnAt);
            command.Parameters.AddWithValue("@fuelFilter", criteria.FuelFilter);
            command.Parameters.AddWithValue("@transmissionFilter", criteria.TransmissionFilter);
            var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));

            const string insertCar = "INSERT INTO Cars (CollectionId, Model, RentalCompany, Transmission, FuelType, Price, Currency, SourceUrl, IsMockData, ScrapedAt) VALUES (@collectionId,@model,@company,@transmission,@fuel,@price,@currency,@url,@mock,@scrapedAt)";
            foreach (var car in cars)
            {
                await using var carCommand = new MySqlCommand(insertCar, connection, transaction);
                carCommand.Parameters.AddWithValue("@collectionId", id);
                carCommand.Parameters.AddWithValue("@model", car.Model);
                carCommand.Parameters.AddWithValue("@company", car.RentalCompany);
                carCommand.Parameters.AddWithValue("@transmission", car.Transmission);
                carCommand.Parameters.AddWithValue("@fuel", car.FuelType);
                carCommand.Parameters.AddWithValue("@price", car.Price);
                carCommand.Parameters.AddWithValue("@currency", car.Currency);
                carCommand.Parameters.AddWithValue("@url", (object?)car.SourceUrl ?? DBNull.Value);
                carCommand.Parameters.AddWithValue("@mock", car.IsMockData);
                carCommand.Parameters.AddWithValue("@scrapedAt", car.ScrapedAt ?? DateTime.UtcNow);
                await carCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new SavedCollection(id, name.Trim(), criteria, DateTime.Now, cars.ToArray());
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = "DELETE FROM Collections WHERE Id=@id AND UserId=@userId";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@userId", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SavedCollection?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureFilterColumnsAsync(connection, cancellationToken);
        const string collectionSql = "SELECT Id, CollectionName, PickupLocation, PickupDate, ReturnDate, FuelFilter, TransmissionFilter, CreatedAt FROM Collections WHERE Id=@id AND UserId=@userId";
        await using var command = new MySqlCommand(collectionSql, connection);
        command.Parameters.AddWithValue("@id", id); command.Parameters.AddWithValue("@userId", userId);
        SavedCollection collection;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            collection = new SavedCollection(reader.GetInt64("Id"), reader.GetString("CollectionName"),
                new SearchCriteria(reader.GetString("PickupLocation"), reader.GetDateTime("PickupDate"), reader.GetDateTime("ReturnDate"))
                {
                    FuelFilter = reader.GetString("FuelFilter"),
                    TransmissionFilter = reader.GetString("TransmissionFilter")
                },
                reader.GetDateTime("CreatedAt"), []);
        }
        const string carsSql = "SELECT Model, RentalCompany, Transmission, FuelType, Price, Currency, SourceUrl, IsMockData, ScrapedAt FROM Cars WHERE CollectionId=@id ORDER BY Id";
        await using var carsCommand = new MySqlCommand(carsSql, connection);
        carsCommand.Parameters.AddWithValue("@id", id);
        var cars = new List<CarOffer>();
        await using var carsReader = await carsCommand.ExecuteReaderAsync(cancellationToken);
        while (await carsReader.ReadAsync(cancellationToken))
            cars.Add(new CarOffer(carsReader.GetString("Model"), carsReader.GetString("RentalCompany"), carsReader.GetString("Transmission"),
                carsReader.GetString("FuelType"), carsReader.GetDecimal("Price"), carsReader.GetString("Currency"),
                carsReader.IsDBNull(carsReader.GetOrdinal("SourceUrl")) ? null : carsReader.GetString("SourceUrl"), carsReader.GetBoolean("IsMockData"))
                { ScrapedAt = carsReader.GetDateTime("ScrapedAt") });
        return collection with { Cars = cars };
    }

    private async Task EnsureFilterColumnsAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        if (_filterSchemaReady) return;
        await _filterSchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_filterSchemaReady) return;
            foreach (var (column, definition) in new[]
            {
                ("FuelFilter", "VARCHAR(32) NOT NULL DEFAULT 'Tümü'"),
                ("TransmissionFilter", "VARCHAR(32) NOT NULL DEFAULT 'Tümü'")
            })
            {
                const string existsSql = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='Collections' AND COLUMN_NAME=@column";
                await using var existsCommand = new MySqlCommand(existsSql, connection);
                existsCommand.Parameters.AddWithValue("@column", column);
                if (Convert.ToInt32(await existsCommand.ExecuteScalarAsync(cancellationToken)) != 0) continue;

                // The identifiers and type definitions are fixed in this method; only the parameterized metadata lookup varies.
                var alterSql = $"ALTER TABLE `Collections` ADD COLUMN `{column}` {definition}";
                try
                {
                    await using var alterCommand = new MySqlCommand(alterSql, connection);
                    await alterCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (MySqlException ex) when (ex.Number == 1060)
                {
                    // Another app instance may have added this column after the metadata check.
                }
            }
            _filterSchemaReady = true;
        }
        finally { _filterSchemaLock.Release(); }
    }
}
