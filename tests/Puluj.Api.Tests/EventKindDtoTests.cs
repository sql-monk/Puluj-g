using Puluj.Api.Services;
using Puluj.Contracts;
using Puluj.Domain.Entities;
using Puluj.Domain.Enums;

namespace Puluj.Api.Tests;

/// <summary>P07 compatibility adapter: the catalog DTO is additive and keeps the legacy enum name for readers of the old contract.</summary>
public sealed class EventKindDtoTests
{
    [Fact]
    public void Catalog_dto_carries_contract_category_and_legacy_enum_name()
    {
        var dto = DtoMapper.EventKind(new EventKind
        {
            EventKindId = 5, Code = "alert.air_raid.ended", NameUk = "Відбій", Category = EventKindCategory.Alert, StateModel = "interval",
            RequiresLocationForMap = true, RenderMode = "area", MapColor = "#43a047", MapIcon = "alert-off",
            MapVisible = true, SortOrder = 21, PolicyVersion = 1,
        });
        Assert.Equal("alert", dto.Category);
        Assert.Equal(nameof(EventType.AlertCancelled), dto.LegacyEventType);
        Assert.Equal(5, dto.Id);

        var noLegacy = DtoMapper.EventKind(new EventKind { EventKindId = 6, Code = "fire.reported", NameUk = "Пожежа", Category = EventKindCategory.Event });
        Assert.Null(noLegacy.LegacyEventType); // no invented enum value for new kinds
        Assert.Equal("event", noLegacy.Category);
    }

    [Fact]
    public void Target_dto_keeps_event_type_and_defaults_kind_code_to_null()
    {
        // Positional record with the new trailing optional parameter: existing constructions compile and read null.
        var dto = new TargetDto(1, DateTimeOffset.UnixEpoch, nameof(EventType.TargetObserved), null, "Unknown", "Unknown", "Low", null, null, null, null,
            null, false, "Rule", null, null, null, null, new SourceDto(1, "s", "s", "Telegram", 0.5, null),
            new RawMessageDto(1, "1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null), null, []);
        Assert.Equal("TargetObserved", dto.EventType);
        Assert.Null(dto.EventKindCode);
        Assert.Equal("target.observed", (dto with { EventKindCode = "target.observed" }).EventKindCode);
    }
}
