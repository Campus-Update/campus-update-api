using CampusUpdate.Domain.Content;

namespace CampusUpdate.Domain.Users;

public static class UserRolePermissions
{
    public static bool IsInstitutionAdministrator(this UserRole role) =>
        role is UserRole.SchoolAdmin or UserRole.LeadSchoolAdmin or UserRole.NewsAdmin or UserRole.CalendarAdmin;

    public static bool CanManageContent(this UserRole role, ContentType type) =>
        role is UserRole.SchoolAdmin or UserRole.SuperAdmin or UserRole.LeadSchoolAdmin ||
        (role == UserRole.NewsAdmin && type is (ContentType.News or ContentType.Announcement)) ||
        (role == UserRole.CalendarAdmin && type == ContentType.Event);

    public static bool CanManageCalendars(this UserRole role) =>
        role is UserRole.SchoolAdmin or UserRole.SuperAdmin or UserRole.LeadSchoolAdmin or UserRole.CalendarAdmin;
}
