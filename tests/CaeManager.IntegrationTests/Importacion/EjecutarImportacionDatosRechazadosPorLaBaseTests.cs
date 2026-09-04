using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// REC-109 (DCR-12 B, 2026-08-24): «la importación admite éxito parcial con
/// errores reportados; las filas inconsistentes no bloquean el archivo
/// completo». El vector real, medido sobre <c>origin/main</c>: a diferencia
/// de <c>Puesto</c>/<c>Telefono</c> en la misma clase (y de
/// <c>RazonSocial</c>/<c>Cnae</c> en <see cref="Empresa"/>), los setters de
/// <c>Nombre</c>/<c>Apellidos</c> de <see cref="Trabajador"/> solo comprueban
/// que el valor no esté vacío — nunca su longitud contra la misma constante
/// (<see cref="Trabajador.LongitudMaximaNombre"/>) que configura la columna
/// en <c>TrabajadorConfiguration</c>. Un Nombre de más de 100 caracteres
/// construye un Trabajador válido en memoria (ninguna
/// <c>ArgumentException</c> lo detiene) y solo revienta al confirmar, dentro
/// del único <c>SaveChangesAsync</c> del handler
/// (<c>OperacionImportacionRepository.GuardarSiOperacionNuevaAsync</c>), que
/// solo atrapa la violación de unicidad de <c>OperacionImportacion</c> — el
/// resto de <c>DbUpdateException</c> se propaga y aborta la transacción
/// entera, arrastrando con ella cualquier otra fila válida del mismo
/// archivo. No es un fallo de dominio (no lanza <c>ArgumentException</c>) ni
/// aparece hasta guardar, así que ninguno de los <c>catch</c> por fila que ya
/// existen en <c>EjecutarImportacionCommandHandler</c> lo detecta.
/// </summary>
public class EjecutarImportacionDatosRechazadosPorLaBaseTests : IAsyncLifetime
{
    private const string NombreEmpresa = "Empresa REC-109 S.L.";
    private const string DniValido = "12345678Z";
    private const string DniDeLaFilaMala = "77189989B";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        contexto.Empresas.Add(new Empresa(NombreEmpresa));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_nombre_que_supera_la_columna_no_bloquea_las_demas_filas_del_archivo()
    {
        // El dominio (Trabajador.DeEmpresa → EstablecerNombre) acepta esto sin
        // rechistar; la columna de Postgres (varchar(100), ver
        // TrabajadorConfiguration) no.
        var nombreDemasiadoLargo = new string('A', Trabajador.LongitudMaximaNombre + 1);

        var plan = new PlanImportacionDto(
            OperacionId: Guid.NewGuid(),
            ClientesCentros: [],
            Empresas: [],
            Trabajadores:
            [
                // Fila buena: debe sobrevivir aunque la fila mala del mismo
                // archivo reviente al guardar.
                new TrabajadorImportadoDto(NombreEmpresa, "Marta", "Ruiz", DniValido, null, null, YaExiste: false),
                // Fila mala: el dominio no la para, la base sí.
                new TrabajadorImportadoDto(NombreEmpresa, nombreDemasiadoLargo, "Pérez", DniDeLaFilaMala, null, null, YaExiste: false),
            ],
            Documentos: [],
            Asignaciones: [],
            Advertencias: [],
            Omitidos: []);

        await using var contexto = CrearContexto();
        var resultado = await ConstruirHandler(contexto).Handle(new EjecutarImportacionCommand(plan), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(
            "una fila que el dominio acepta y la base rechaza no puede tumbar la confirmación entera (DCR-12 B)");
        resultado.Valor.TrabajadoresCreados.Should().Be(1,
            "la fila válida (Marta Ruiz) debe importarse aunque la otra fila del mismo archivo falle en la base");
        resultado.Valor.Omitidos.Should().Contain(o => o.Descripcion.Contains(DniDeLaFilaMala) && o.Motivo.Contains("100"),
            "el motivo del descarte debe llegar al informe de omitidos — nada se descarta en silencio (IMPORTACION.md § 3 bis)");

        await using var verificacion = CrearContexto();
        (await verificacion.Trabajadores.CountAsync(t => t.Dni == DniValido)).Should().Be(1,
            "la fila buena no puede perderse por culpa de la fila mala del mismo archivo");
        (await verificacion.Trabajadores.CountAsync(t => t.Dni == DniDeLaFilaMala)).Should().Be(0,
            "la fila mala, precisamente, no debe llegar a persistirse");
    }

    /// <summary>
    /// Mismo defecto, vector gemelo: hallado por la revisión adversarial de
    /// este mismo incremento — <c>Trabajador.Email</c> lo rellena la misma
    /// fila del mismo Excel (<c>fila.Email</c> en
    /// <c>EjecutarImportacionCommand.cs</c>) y tenía el mismo hueco que
    /// <c>Nombre</c>/<c>Apellidos</c>: ninguna comprobación de longitud en
    /// el dominio contra la misma columna <c>varchar(200)</c> de
    /// <c>TrabajadorConfiguration</c>.
    /// </summary>
    [Fact]
    public async Task Un_email_que_supera_la_columna_no_bloquea_las_demas_filas_del_archivo()
    {
        var emailDemasiadoLargo = new string('a', Trabajador.LongitudMaximaEmail + 1) + "@x.com";

        var plan = new PlanImportacionDto(
            OperacionId: Guid.NewGuid(),
            ClientesCentros: [],
            Empresas: [],
            Trabajadores:
            [
                new TrabajadorImportadoDto(NombreEmpresa, "Marta", "Ruiz", DniValido, null, null, YaExiste: false),
                new TrabajadorImportadoDto(NombreEmpresa, "Luis", "Pérez", DniDeLaFilaMala, null, emailDemasiadoLargo, YaExiste: false),
            ],
            Documentos: [],
            Asignaciones: [],
            Advertencias: [],
            Omitidos: []);

        await using var contexto = CrearContexto();
        var resultado = await ConstruirHandler(contexto).Handle(new EjecutarImportacionCommand(plan), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(
            "un email que el dominio acepta y la base rechaza no puede tumbar la confirmación entera (DCR-12 B)");
        resultado.Valor.TrabajadoresCreados.Should().Be(1,
            "la fila válida (Marta Ruiz) debe importarse aunque la otra fila del mismo archivo falle en la base");
        resultado.Valor.Omitidos.Should().Contain(o => o.Descripcion.Contains(DniDeLaFilaMala) && o.Motivo.Contains("200"),
            "el motivo del descarte debe llegar al informe de omitidos — nada se descarta en silencio (IMPORTACION.md § 3 bis)");

        await using var verificacion = CrearContexto();
        (await verificacion.Trabajadores.CountAsync(t => t.Dni == DniValido)).Should().Be(1,
            "la fila buena no puede perderse por culpa de la fila mala del mismo archivo");
        (await verificacion.Trabajadores.CountAsync(t => t.Dni == DniDeLaFilaMala)).Should().Be(0,
            "la fila mala, precisamente, no debe llegar a persistirse");
    }

    private static EjecutarImportacionCommandHandler ConstruirHandler(CaeManagerDbContext contexto) =>
        new(
            new EmpresaRepository(contexto), new TrabajadorRepository(contexto), new DocumentoRepository(contexto),
            new AsignacionRepository(contexto), new OperacionImportacionRepository(contexto),
            contexto, contexto, contexto, contexto, contexto, contexto);

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql =>
            {
                npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");
                // Fiel a ConfiguracionDeContexto.Aplicar (producción real) — ver
                // hydra-postgres-retry-strategy-vs-transaccion-explicita.
                npgsql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(30), errorCodesToAdd: null);
            })
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }
}
