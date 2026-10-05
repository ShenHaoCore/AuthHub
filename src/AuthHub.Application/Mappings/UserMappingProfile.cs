using AuthHub.Application.DTOs.Users;
using AuthHub.Domain.Entities;
using AutoMapper;

namespace AuthHub.Application.Mappings;

/// <summary>
/// 用户实体 → DTO。
/// <c>Roles</c> / <c>LockedOut</c> 无法从实体直接得到（分别来自角色表和锁定期），
/// 由调用方在映射后补齐，因此这里显式忽略，避免 AutoMapper 抛配置校验错误。
/// </summary>
public sealed class UserMappingProfile : Profile
{
    public UserMappingProfile()
    {
        CreateMap<ApplicationUser, UserDto>()
            .ForMember(d => d.Roles, o => o.Ignore())
            .ForMember(d => d.LockedOut, o => o.Ignore())
            .ForMember(d => d.DisplayName, o => o.MapFrom(s => s.DisplayName == string.Empty ? s.UserName : s.DisplayName));

        CreateMap<ApplicationRole, DTOs.Roles.RoleDto>()
            .ForMember(d => d.Permissions, o => o.Ignore())
            .ForMember(d => d.UserCount, o => o.Ignore());
    }
}
