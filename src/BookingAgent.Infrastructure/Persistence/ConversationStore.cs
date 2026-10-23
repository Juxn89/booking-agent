namespace BookingAgent.Infrastructure.Persistence;

using BookingAgent.Application.Features.Assistant;
using BookingAgent.Domain.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;

/// <summary>
/// Conversations and their steps. A conversation is read without tracking and attached only to save it: the tools
/// save bookings through the same context in between, and a failed booking save clears whatever it tracks.
/// </summary>
/// <param name="db">The database.</param>
internal sealed class ConversationStore(BookingDbContext db) : IConversationStore
{
	public Task<AgentConversation?> FindAsync(Guid id, CancellationToken cancellationToken) =>
		db.AgentConversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

	public Task<decimal> SpentSinceAsync(Instant since, CancellationToken cancellationToken) =>
		db.AgentSteps.Where(s => s.At >= since).SumAsync(static s => s.CostUsd, cancellationToken);

	public async Task SaveTurnAsync(AgentConversation conversation, bool isNew, IReadOnlyList<AgentStep> steps, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(conversation);
		if (isNew)
		{
			db.AgentConversations.Add(conversation);
		}
		else
		{
			// A turn adds exactly one to Turns: the row must still say what it said when this turn read it.
			var entry = db.AgentConversations.Attach(conversation);
			entry.State = EntityState.Modified;
			entry.Property(static c => c.Turns).OriginalValue = conversation.Turns - 1;
		}

		db.AgentSteps.AddRange(steps);
		try
		{
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException || IsDuplicate(exception))
		{
			db.ChangeTracker.Clear();
			throw new ConversationBusyException("Another message of the conversation was saved first.", exception);
		}
	}

	// Two first messages racing to create the same conversation cannot happen (the id is the server's), so a
	// duplicate key here is the same race as a concurrency conflict on an existing one.
	private static bool IsDuplicate(DbUpdateException exception) =>
		exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };
}

internal sealed class AgentConversationConfiguration : IEntityTypeConfiguration<AgentConversation>
{
	public void Configure(EntityTypeBuilder<AgentConversation> builder)
	{
		builder.ToTable("agent_conversations");
		builder.HasKey(c => c.Id);
		builder.Property(c => c.Id).ValueGeneratedNever();
		builder.HasOne<Domain.Catalog.Business>().WithMany().HasForeignKey(c => c.BusinessId);

		// Text, not jsonb: jsonb reorders keys, and the serialiser needs each content's "$type" first.
		builder.Property(c => c.History);
		builder.Property(c => c.CostUsd).HasColumnType(Columns.Usd);

		// Two messages of one conversation answered at once: the second save must lose.
		builder.Property(c => c.Turns).IsConcurrencyToken();
	}
}

internal sealed class AgentStepConfiguration : IEntityTypeConfiguration<AgentStep>
{
	public void Configure(EntityTypeBuilder<AgentStep> builder)
	{
		builder.ToTable("agent_steps");
		builder.HasKey(s => s.Id);
		builder.Property(s => s.Id).ValueGeneratedNever();
		builder.HasOne<AgentConversation>().WithMany().HasForeignKey(s => s.ConversationId);
		builder.Property(s => s.Kind).HasConversion<string>().HasMaxLength(Columns.Enum);
		builder.Property(s => s.ToolName).HasMaxLength(Columns.Name);
		builder.Property(s => s.Input).HasColumnType("jsonb");
		builder.Property(s => s.Output).HasColumnType("jsonb");
		builder.Property(s => s.CostUsd).HasColumnType(Columns.Usd);
		builder.HasIndex(s => new { s.ConversationId, s.Turn, s.Ordinal });

		// The daily budget sums the cost since midnight.
		builder.HasIndex(s => s.At);
	}
}
