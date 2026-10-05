using AuthHub.Application.DTOs.AuditLogs;
using AuthHub.Domain.Entities;
using AutoMapper;

namespace AuthHub.Application.Mappings;

public sealed class AuditLogMappingProfile : Profile
{
    public AuditLogMappingProfile()
    {
        CreateMap<AuditLog, AuditLogDto>();
    }
}
