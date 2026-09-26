using CampusUpdate.Api.Contracts;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Route("api/v1/schools")]
public sealed class SchoolsController(CampusUpdateDbContext db) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<InstitutionResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<InstitutionResponse>>> Get(CancellationToken cancellationToken)
    {
        var schools = await db.Institutions
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .Select(institution => new InstitutionResponse(
                institution.Id,
                institution.Name,
                institution.Slug,
                institution.Acronym,
                institution.LogoUrl,
                institution.Faculties.OrderBy(x => x.Name).Select(faculty => new FacultyResponse(
                    faculty.Id,
                    faculty.Name,
                    faculty.Code,
                    faculty.Departments.OrderBy(x => x.Name).Select(department => new DepartmentResponse(
                        department.Id,
                        department.Name,
                        department.Code,
                        department.Programmes.OrderBy(x => x.Name).Select(programme => new ProgrammeResponse(
                            programme.Id,
                            programme.Name,
                            programme.Code,
                            programme.Levels.OrderBy(x => x.SortOrder).Select(level =>
                                new AcademicLevelResponse(level.Id, level.Name, level.SortOrder)).ToArray()
                        )).ToArray()
                    )).ToArray()
                )).ToArray()
            ))
            .ToListAsync(cancellationToken);

        return Ok(schools);
    }
}
