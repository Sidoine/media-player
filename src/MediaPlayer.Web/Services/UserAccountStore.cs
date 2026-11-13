using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace media_player.Services;

public sealed class UserAccountStore(IDbContextFactory<UserDbContext> contexts)
{
    private readonly PasswordHasher<UserAccount> hasher = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
    }

    public async Task<IReadOnlyList<UserAccount>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().OrderBy(user => user.UserName).ToListAsync(ct);
    }

    public async Task<UserAccount?> FindAsync(string username, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(user => user.UserName == username, ct);
    }

    public async Task<UserAccount> CreateAsync(string username, string password, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            var role = await db.Users.CountAsync(ct) == 0 ? "Administrator" : "User";
            var user = new UserAccount { UserName = username.Trim(), Role = role };
            user.PasswordHash = hasher.HashPassword(user, password);
            db.Users.Add(user);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) { throw new InvalidOperationException("Cet utilisateur existe déjà.", ex); }
            await transaction.CommitAsync(ct);
            return user;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> VerifyAsync(string username, string password, CancellationToken ct = default)
    {
        var user = await FindAsync(username, ct);
        return user is not null && hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
    }
}
