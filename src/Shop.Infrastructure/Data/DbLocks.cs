using Microsoft.EntityFrameworkCore;

namespace Shop.Infrastructure.Data;

internal static class DbLocks
{
    // Waits for a database lock that is released when the current transaction ends. Must be called inside a transaction.
    // Everything that changes one order (status change, payment answer, expiry) uses "order:{number}", so these never run at the same moment.
    public static Task AcquireAsync(this ShopDbContext db, string resource)
    {
        return db.Database.ExecuteSqlAsync(
            $"EXEC sp_getapplock @Resource = {resource}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000");
    }
}
