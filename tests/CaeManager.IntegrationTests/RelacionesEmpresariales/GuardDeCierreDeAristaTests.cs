using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Commands.EditarEmpresa;
using CaeManager.Application.RelacionesEmpresariales;
using CaeManager.Application.Subcontratas.Commands.EditarSubcontrata;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Trabajadores;
using CaeManager.Application.Plataforma;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.RelacionesEmpresariales;

/// <summary>
/// <b>R2 — el guard que el diseño de F5 § 5.4(a) no podía escribir.</b>
///
/// <para>
/// Aquel guard se formulaba <i>«bloquea si existe un Centro vivo con
/// <c>ClienteId = C</c> y <c>EmpresaId = P</c>»</i>. Para la arista de PD-4
/// (subcontrata → contratista) <b>esa fila no existe ni puede existir</b>,
/// porque en <c>Centros</c> el <c>ClienteId</c> es el titular y el
/// <c>EmpresaId</c> la contratista. La única defensa diseñada no cubría el
/// caso que motivó la reapertura, y romperlo era <b>un clic</b>: abrir la
/// ficha de la subcontrata, desmarcar la contratista, guardar.
/// </para>
/// <para>
/// Estos tests van contra PostgreSQL real a propósito: la propiedad que
/// importa es la <b>consulta</b>, y un doble la reimplementaría en vez de
/// comprobarla.
/// </para>
/// </summary>
public class GuardDeCierreDeAristaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _titularId;      // dueño del centro
    private Guid _contratistaId;  // opera el centro
    private Guid _subcontrataId;  // trabaja bajo la contratista
    private Guid _centroId;
    private Guid _trabajadorDeLaSubcontrataId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var titular = Empresa.CrearComoCliente("Titular del Guard S.A.", "B12345674", false, null, null);
        var contratista = new Empresa("Contratista del Guard S.L.", "B87654323");
        var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata del Guard S.L.", "B10380210", "Supervisada");
        contexto.Empresas.AddRange(titular, contratista, subcontrata);
        await contexto.SaveChangesAsync();

        _titularId = titular.Id;
        _contratistaId = contratista.Id;
        _subcontrataId = subcontrata.Id;

        // El centro es del titular y lo opera la contratista.
        var centro = new Centro(titular.Id, contratista.Id, "Centro del Guard");
        contexto.Centros.Add(centro);

        // Un trabajador DE LA SUBCONTRATA, que es quien crea la dependencia
        // que el guard viejo no podía ver.
        var trabajador = Trabajador.DeSubcontrata(subcontrata.Id, "Ana", "García", "77189989B");
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        _centroId = centro.Id;
        _trabajadorDeLaSubcontrataId = trabajador.Id;

        // Las dos aristas vivas: contratista→titular y subcontrata→contratista.
        var ahora = DateTime.UtcNow;
        contexto.RelacionesEmpresariales.AddRange(
            RelacionEmpresarial.Crear(contratista.Id, titular.Id, ahora),
            RelacionEmpresarial.Crear(subcontrata.Id, contratista.Id, ahora));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ---------- La arista de PD-4: el clic que rompía P0 ----------

    [Fact]
    public async Task Desmarcar_la_contratista_se_bloquea_si_la_subcontrata_tiene_gente_en_sus_centros()
    {
        await AsignarAlCentroAsync(_trabajadorDeLaSubcontrataId, _centroId, cerrada: false);

        await using (var contexto = CrearContexto())
        {
            var resultado = await EditarSubcontrata(contexto).Handle(
                // Se desmarca la contratista: EmpresaIds llega vacío.
                new EditarSubcontrataCommand(_subcontrataId, "Subcontrata del Guard S.L.", "B10380210", [], []),
                CancellationToken.None);

            resultado.EsFallido.Should().BeTrue("un clic no puede dejar huérfana la operación viva");
            resultado.Error.Codigo.Should().Be("Subcontrata.AristaConOperacionViva");
        }

        await using var verificacion = CrearContexto();
        var sigueVigente = await verificacion.RelacionesEmpresariales
            .AnyAsync(r => r.ProveedoraId == _subcontrataId && r.ClienteId == _contratistaId && r.VigenciaHasta == null);
        sigueVigente.Should().BeTrue("bloquear significa no cerrar, no cerrar y avisar");
    }

    [Fact]
    public async Task Desmarcar_la_contratista_se_permite_si_la_asignacion_ya_estaba_cerrada()
    {
        // Misma forma, con la asignación dada de baja: ya no hay operación
        // viva que proteger, así que el guard NO debe bloquear. Sin este
        // caso, un guard que dijera "siempre" pasaría el test de arriba.
        await AsignarAlCentroAsync(_trabajadorDeLaSubcontrataId, _centroId, cerrada: true);

        await using (var contexto = CrearContexto())
        {
            var resultado = await EditarSubcontrata(contexto).Handle(
                new EditarSubcontrataCommand(_subcontrataId, "Subcontrata del Guard S.L.", "B10380210", [], []),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var verificacion = CrearContexto();
        var sigueVigente = await verificacion.RelacionesEmpresariales
            .AnyAsync(r => r.ProveedoraId == _subcontrataId && r.ClienteId == _contratistaId && r.VigenciaHasta == null);
        sigueVigente.Should().BeFalse("sin operación viva, la baja es legítima");
    }

    [Fact]
    public async Task Desmarcar_la_contratista_se_permite_si_la_subcontrata_no_tiene_a_nadie_alli()
    {
        // Sin ninguna asignación: el guard no puede inventarse dependencia.
        await using (var contexto = CrearContexto())
        {
            var resultado = await EditarSubcontrata(contexto).Handle(
                new EditarSubcontrataCommand(_subcontrataId, "Subcontrata del Guard S.L.", "B10380210", [], []),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var verificacion = CrearContexto();
        var sigueVigente = await verificacion.RelacionesEmpresariales
            .AnyAsync(r => r.ProveedoraId == _subcontrataId && r.ClienteId == _contratistaId && r.VigenciaHasta == null);
        sigueVigente.Should().BeFalse();
    }

    // ---------- La arista ordinaria ----------

    [Fact]
    public async Task Desmarcar_al_titular_se_bloquea_mientras_la_contratista_opere_un_centro_suyo()
    {
        await using (var contexto = CrearContexto())
        {
            var handler = new EditarEmpresaCommandHandler(
                new EmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto), contexto,
                new GuardDeCierreDeArista(contexto, contexto, contexto),
                CrearAlcanceConAccesoTotal(contexto), contexto);

            var resultado = await handler.Handle(
                new EditarEmpresaCommand(_contratistaId, "Contratista del Guard S.L.", "B87654323", []),
                CancellationToken.None);

            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be("Empresa.AristaConOperacionViva");
        }

        await using var verificacion = CrearContexto();
        var sigueVigente = await verificacion.RelacionesEmpresariales
            .AnyAsync(r => r.ProveedoraId == _contratistaId && r.ClienteId == _titularId && r.VigenciaHasta == null);
        sigueVigente.Should().BeTrue();
    }

    // ---------- Que no bloquee de más ----------

    [Fact]
    public async Task El_guard_no_confunde_las_dos_direcciones_de_la_arista()
    {
        // La dependencia es (subcontrata → contratista). Preguntar por el par
        // invertido (contratista → subcontrata) debe dar false: si el guard
        // ignorase la dirección, bloquearía ediciones legítimas y sería
        // imposible cerrar ninguna arista de un par que opere junto.
        await AsignarAlCentroAsync(_trabajadorDeLaSubcontrataId, _centroId, cerrada: false);

        await using var contexto = CrearContexto();
        var guard = new GuardDeCierreDeArista(contexto, contexto, contexto);

        (await guard.TieneOperacionVivaAsync(_subcontrataId, _contratistaId)).Should().BeTrue();
        (await guard.TieneOperacionVivaAsync(_contratistaId, _subcontrataId)).Should().BeFalse();
    }

    [Fact]
    public async Task El_guard_no_bloquea_por_un_tercero_ajeno_a_la_arista()
    {
        await AsignarAlCentroAsync(_trabajadorDeLaSubcontrataId, _centroId, cerrada: false);

        await using var contexto = CrearContexto();
        var guard = new GuardDeCierreDeArista(contexto, contexto, contexto);

        // Una empresa que no participa en nada de esto.
        (await guard.TieneOperacionVivaAsync(_subcontrataId, Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task El_guard_no_bloquea_por_trabajadores_que_no_son_de_la_subcontrata()
    {
        // Lo destapó una mutación que sobrevivió: el filtro de dirección lo
        // hacía el predicado de CENTRO, y el de TRABAJADOR no lo comprobaba
        // nadie. Sin este caso, un guard que bloqueara por "hay alguien en
        // ese centro" —sea de quien sea— pasaría todos los demás tests, y
        // desvincular una subcontrata que ya se fue sería imposible mientras
        // quedase cualquier otra persona en el centro.
        var trabajadorDeLaContratista = Trabajador.DeEmpresa(_contratistaId, "Luis", "Pérez", "12345678Z");
        await using (var contexto = CrearContexto())
        {
            contexto.Trabajadores.Add(trabajadorDeLaContratista);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajadorDeLaContratista.Id, _centroId, new DateOnly(2026, 1, 15)));
            await contexto.SaveChangesAsync();
        }

        await using var verificacion = CrearContexto();
        var guard = new GuardDeCierreDeArista(verificacion, verificacion, verificacion);

        // Hay una asignación activa en el centro de la contratista, pero no
        // es de la subcontrata: su arista puede cerrarse.
        (await guard.TieneOperacionVivaAsync(_subcontrataId, _contratistaId)).Should().BeFalse();
    }

    // ---------- Utilidades ----------

    private async Task AsignarAlCentroAsync(Guid trabajadorId, Guid centroId, bool cerrada)
    {
        await using var contexto = CrearContexto();
        var asignacion = new Asignacion(trabajadorId, centroId, new DateOnly(2026, 1, 15));
        if (cerrada)
            asignacion.DarDeBaja(new DateOnly(2026, 6, 30));
        contexto.Asignaciones.Add(asignacion);
        await contexto.SaveChangesAsync();
    }

    private EditarSubcontrataCommandHandler EditarSubcontrata(CaeManagerDbContext contexto) =>
        new(new EmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto), contexto,
            new GuardDeCierreDeArista(contexto, contexto, contexto),
            CrearAlcanceConAccesoTotal(contexto), contexto);

    private IAlcanceDatosService CrearAlcanceConAccesoTotal(CaeManagerDbContext contexto) =>
        new AlcanceDatosService(
            contexto, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"),
            new TenantActualAmbiental { TenantId = _tenant },
            new SesionPrivilegiadaAusente());

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
