using System.Text.Json;
using EquipTrack.Api.Auth;
using EquipTrack.Api.Data;

namespace EquipTrack.Api.Organizations;

public static class Audit
{
    /// <summary>Adds an audit row to the context (saved with the caller's SaveChanges).</summary>
    public static void Record(
        AppDbContext db, Guid organizationId, MemberContext actor, string action,
        string targetType, string targetId, string targetLabel, DateTimeOffset now, object? metadata = null) =>
        Record(db, organizationId, actor.UserId, actor.Name, actor.Role, action, targetType, targetId, targetLabel, now, metadata);

    public static void Record(
        AppDbContext db, Guid organizationId, string actorUserId, string actorName, string actorRole, string action,
        string targetType, string targetId, string targetLabel, DateTimeOffset now, object? metadata = null)
    {
        db.OrgAuditEvents.Add(new OrgAuditEvent
        {
            OrganizationId = organizationId,
            OccurredAt = now,
            ActorUserId = actorUserId,
            ActorName = Trunc(actorName, 200),
            ActorRole = actorRole,
            Action = action,
            TargetType = targetType,
            TargetId = Trunc(targetId, 100),
            TargetLabel = Trunc(targetLabel, 300),
            Metadata = metadata is null ? "{}" : JsonSerializer.Serialize(metadata, JsonOptions),
        });
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string Trunc(string value, int max) => value.Length <= max ? value : value[..max];
}
