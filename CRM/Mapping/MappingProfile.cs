using AutoMapper;
using CRM.DTOs.Requests;
using CRM.DTOs.Responses;
using CRM.Models;

namespace CRM.Mapping;

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        // Validate every request field; IDs and navigation properties are server-managed.
        CreateMap<RoleCreateRequest, Role>(MemberList.Source);
        CreateMap<RoleUpdateRequest, Role>(MemberList.Source);
        CreateMap<Role, RoleResponse>();
        CreateMap<Role, UserRoleResponse>();

        CreateMap<UserCreateRequest, User>(MemberList.Source);
        CreateMap<UserUpdateRequest, User>(MemberList.Source);
        CreateMap<User, UserResponse>();
        CreateMap<User, NoteAuthorResponse>();
        CreateMap<User, OpportunityAgentResponse>();
        CreateMap<User, PropertyListingAgentResponse>();

        CreateMap<ContactCreateRequest, Contact>(MemberList.Source);
        CreateMap<ContactUpdateRequest, Contact>(MemberList.Source);
        CreateMap<Contact, ContactResponse>();
        CreateMap<User, ContactOwnerResponse>();
        CreateMap<Contact, OpportunityBuyerContactResponse>();
        CreateMap<Contact, PropertySellerContactResponse>();


        CreateMap<PipelineStageCreateRequest, PipelineStage>(MemberList.Source);
        CreateMap<PipelineStageUpdateRequest, PipelineStage>(MemberList.Source);
        CreateMap<PipelineStage, PipelineStageResponse>();
        CreateMap<PipelineStage, OpportunityStageResponse>();

        CreateMap<OpportunityCreateRequest, Opportunity>(MemberList.Source);
        CreateMap<OpportunityUpdateRequest, Opportunity>(MemberList.Source);
        CreateMap<Opportunity, OpportunityResponse>()
            .ForMember(d => d.CommissionAmount, o => o.MapFrom(s => s.Status == "Won" && s.FinalPrice.HasValue
                ? (decimal?)decimal.Round(s.FinalPrice.Value * s.CommissionRate, 2, MidpointRounding.AwayFromZero) : null));

        CreateMap<ActivityCreateRequest, Activity>(MemberList.Source);
        CreateMap<ActivityUpdateRequest, Activity>(MemberList.Source);
        CreateMap<Activity, ActivityResponse>();

        CreateMap<NoteCreateRequest, Note>(MemberList.Source);
        CreateMap<NoteUpdateRequest, Note>(MemberList.Source);
        CreateMap<Note, NoteResponse>();

        CreateMap<PropertyCreateRequest, Property>(MemberList.Source);
        CreateMap<PropertyUpdateRequest, Property>(MemberList.Source);
        CreateMap<Property, PropertyResponse>();
        CreateMap<Property, OpportunityPropertyResponse>();
    }
}


