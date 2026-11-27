using System.ComponentModel.DataAnnotations;
using AutoMapper;
using CRM.DTOs.Requests;
using CRM.DTOs.Responses;
using CRM.Mapping;
using CRM.Models;
using Xunit;

namespace CRM.Tests;

public class ContractTests
{
    private static readonly MapperConfiguration Mapping = new(c => c.AddProfile<MappingProfile>());
    [Fact]
    public void All_request_and_response_maps_are_valid() => Mapping.AssertConfigurationIsValid();

    [Theory]
    [InlineData("Buyer", true)]
    [InlineData("Seller", true)]
    [InlineData("Both", true)]
    [InlineData("Prospect", false)]
    [InlineData("", false)]
    public void Contacts_use_v2_types(string type, bool valid)
    {
        var request = Contact(); request.Type = type;
        Assert.Equal(valid, IsValid(request));
        Assert.Equal(valid, IsValid(new CRM.Models.Contact { Type = type, FirstName = "Alex", LastName = "Lee", Email = "alex@example.com", Source = "Website", OwnerId = 1 }));
    }

    [Fact]
    public void Contact_requires_last_name_contact_channel_and_known_source()
    {
        var request = Contact();
        request.LastName = ""; Assert.False(IsValid(request));
        request.LastName = "Lee"; request.Email = null; Assert.False(IsValid(request));
        request.Phone = "+639171234567"; Assert.True(IsValid(request));
        request.Source = "Open house"; Assert.False(IsValid(request));
    }

    [Theory]
    [InlineData("Draft", true)]
    [InlineData("Available", true)]
    [InlineData("Reserved", true)]
    [InlineData("Sold", true)]
    [InlineData("Withdrawn", true)]
    [InlineData("Active", false)]
    [InlineData("UnderContract", false)]
    public void Properties_use_v2_statuses(string status, bool valid)
    {
        Assert.Equal(valid, IsValid(new PropertyCreateRequest { Address = "1 Main St", City = "Manila", PropertyType = "House", Price = 100, Status = status, SellerContactId = 1, ListingAgentId = 2 }));
    }

    [Fact]
    public void Draft_price_and_bedrooms_are_optional_but_publication_needs_positive_price()
    {
        var request = new PropertyCreateRequest { Address = "1 Main St", City = "Manila", PropertyType = "Lot", SellerContactId = 1, ListingAgentId = 2 };
        Assert.True(IsValid(request));
        request.Status = "Available"; Assert.False(IsValid(request));
        request.Price = 0; Assert.False(IsValid(request));
        request.Price = 100; Assert.True(IsValid(request));
        request.Bedrooms = -1; Assert.False(IsValid(request));
        request.Bedrooms = null; request.City = ""; Assert.False(IsValid(request));
    }

    [Fact]
    public void Property_type_length_matches_persisted_fifty_character_limit()
    {
        var request = new PropertyCreateRequest { Address = "1 Main St", City = "Manila", PropertyType = new string('x', 50), SellerContactId = 1, ListingAgentId = 2 };
        Assert.True(IsValid(request));
        request.PropertyType += "x";
        Assert.False(IsValid(request));
    }

    [Theory]
    [InlineData("Contact", 1, true)]
    [InlineData("Property", 2, true)]
    [InlineData("Opportunity", 3, true)]
    [InlineData("User", 1, false)]
    [InlineData("Contact", 0, false)]
    [InlineData("Property", -1, false)]
    public void Note_targets_reject_invalid_types_and_keys_before_persistence(string kind, int id, bool valid)
    {
        Assert.Equal(valid, IsValid(new NoteCreateRequest { AuthorId = 1, EntityType = kind, EntityId = id, Body = "Details" }));
        Assert.Equal(valid, IsValid(new NoteUpdateRequest { AuthorId = 1, EntityType = kind, EntityId = id, Body = "Details" }));
    }

    [Fact]
    public void Activities_support_contact_only_scheduling_and_reject_generated_external_types()
    {
        var request = new ActivityCreateRequest { Type = "Task", Subject = "Call buyer", ContactId = 1, UserId = 2, ScheduledAt = DateTime.UtcNow.AddDays(1) };
        Assert.True(IsValid(request));
        request.ScheduledAt = null; Assert.False(IsValid(request));
        request.CompletedAt = DateTime.UtcNow; Assert.True(IsValid(request));
        request.Type = "System"; Assert.False(IsValid(request));
        request.Type = "Stage Change"; Assert.False(IsValid(request));
        request.Type = "Viewing"; Assert.False(IsValid(request));
        request.OpportunityId = 3; Assert.True(IsValid(request));
        request.CompletedAt = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified); Assert.False(IsValid(request));
    }

    [Fact]
    public void Opportunity_offer_is_optional_before_offer_stage_and_fields_map_without_outcome_overwrite()
    {
        var request = new OpportunityCreateRequest { BuyerContactId = 1, PropertyId = 2, AgentId = 3, StageId = 1 };
        Assert.True(IsValid(request));
        Assert.False(IsValid(new OpportunityCreateRequest()));
        var entity = Mapping.CreateMapper().Map<Opportunity>(request);
        Assert.Null(entity.OfferAmount); Assert.Equal("Open", entity.Status); Assert.Null(entity.FinalPrice);
        request.OfferAmount = -1; Assert.False(IsValid(request));
    }

    [Fact]
    public void V2_response_fields_map_and_serialize_without_navigation_cycles()
    {
        var mapper = Mapping.CreateMapper();
        var entity = new Activity { Id = 1, Subject = "Call", Type = "Call", ContactId = 2, UserId = 3, CreatedById = 4, CompletedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };
        var response = mapper.Map<ActivityResponse>(entity);
        Assert.Equal(4, response.CreatedById); Assert.Equal("Call", response.Subject); Assert.Null(response.OpportunityId);
        Assert.NotEmpty(System.Text.Json.JsonSerializer.Serialize(response));
        var property = mapper.Map<PropertyResponse>(new Property { City = "Manila", PropertyType = "Lot", Bedrooms = null, Price = null });
        Assert.Equal("Manila", property.City); Assert.Null(property.Price); Assert.Null(property.Bedrooms);
        var user = mapper.Map<UserResponse>(new User { Phone = "+639171234567", IsActive = false });
        Assert.Equal("+639171234567", user.Phone); Assert.False(user.IsActive);
        var stage = mapper.Map<PipelineStageResponse>(new PipelineStage { Code = "INQUIRY", Name = "Inquiry", SortOrder = 1 });
        Assert.Equal("INQUIRY", stage.Code);
    }

    private static ContactCreateRequest Contact() => new() { Type = "Buyer", FirstName = "Alex", LastName = "Lee", Email = "alex@example.com", Source = "Website", OwnerId = 1 };
    private static bool IsValid(object value) => Validator.TryValidateObject(value, new ValidationContext(value), new List<ValidationResult>(), true);
}
