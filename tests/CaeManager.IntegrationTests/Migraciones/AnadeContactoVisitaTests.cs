using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// El contacto de la Visita es, por defecto, el del Centro (decisión de producto, 2026-10-16). Las Visitas que ya
/// existen cuando se despliega <c>AnadeContactoVisita</c> lo reciben de la propia migración, con el mismo criterio que
/// <see cref="Visita.RellenarContactoDesdeCentro"/>: el texto recortado va al nombre, o al correo si el texto entero es
/// un correo válido. Una Visita cancelada, o de un Centro sin contacto, queda sin contacto.
///
/// <para>
/// La base se siembra con el modelo actual y se deshace hasta la migración anterior: las filas siguen, las tres
/// columnas desaparecen y vuelven a nacer vacías al aplicar la migración, como en staging y producción.
/// </para>
/// </summary>
public class AnadeContactoVisitaTests : IAsyncLifetime
{
    private const string MigracionDelCambio = "20261016232631_AnadeContactoVisita";

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _visitaConNombre;
    private Guid _visitaConCorreo;
    private Guid _visitaCancelada;
    private Guid _visitaDeCentroSinContacto;

    public async Task InitializeAsync()
    {
        var tenant = new TenantPorAmbito();
        await using (var contexto = NuevoContexto(tenant))
        {
            await contexto.Database.MigrateAsync();
            var tenantPropietario = new Tenant("Tenant propietario");
            contexto.Tenants.Add(tenantPropietario);
            await contexto.SaveChangesAsync();

            using var ambito = AmbitoTenantExplicito.Establecer(tenantPropietario.Id);
            contexto.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
            var cliente = Empresa.CrearComoCliente("Cliente Springfield", "B12345674", false, null, null);
            var empresa = new Empresa("Montajes Springfield", "B10380186");
            contexto.Empresas.AddRange(cliente, empresa);
            await contexto.SaveChangesAsync();

            var conNombre = new Centro(cliente.Id, empresa.Id, "Planta norte", contacto: "  Pedro Salas · 954 555 010  ");
            var conCorreo = new Centro(cliente.Id, empresa.Id, "Planta sur", contacto: "accesos@planta.example");
            var sinContacto = new Centro(cliente.Id, empresa.Id, "Planta este");
            contexto.Centros.AddRange(conNombre, conCorreo, sinContacto);
            await contexto.SaveChangesAsync();

            var visitaConNombre = NuevaVisita(conNombre);
            var visitaConCorreo = NuevaVisita(conCorreo);
            var visitaCancelada = NuevaVisita(conNombre);
            visitaCancelada.Cancelar(DateTime.UtcNow, null);
            var visitaDeCentroSinContacto = NuevaVisita(sinContacto);
            contexto.Visitas.AddRange(visitaConNombre, visitaConCorreo, visitaCancelada, visitaDeCentroSinContacto);
            await contexto.SaveChangesAsync();

            _visitaConNombre = visitaConNombre.Id;
            _visitaConCorreo = visitaConCorreo.Id;
            _visitaCancelada = visitaCancelada.Id;
            _visitaDeCentroSinContacto = visitaDeCentroSinContacto.Id;

            var migraciones = contexto.Database.GetMigrations().ToList();
            var indice = migraciones.IndexOf(MigracionDelCambio);
            indice.Should().BeGreaterThan(0, "la migración del cambio existe en el ensamblado y no es la línea base");
            await contexto.GetService<IMigrator>().MigrateAsync(migraciones[indice - 1]);
        }
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Las_visitas_existentes_reciben_el_contacto_de_su_Centro_con_el_mismo_criterio_que_al_crearlas()
    {
        await using (var contexto = NuevoContexto(new TenantPorAmbito()))
            await contexto.GetService<IMigrator>().MigrateAsync();

        await using var lectura = NuevoContexto(new TenantPorAmbito());
        var visitas = await lectura.Visitas.IgnoreQueryFilters()
            .Select(v => new { v.Id, v.ContactoNombre, v.ContactoTelefono, v.ContactoCorreo })
            .ToDictionaryAsync(v => v.Id);

        visitas[_visitaConNombre].Should().BeEquivalentTo(new { ContactoNombre = "Pedro Salas · 954 555 010", ContactoTelefono = (string?)null, ContactoCorreo = (string?)null });
        visitas[_visitaConCorreo].Should().BeEquivalentTo(new { ContactoNombre = (string?)null, ContactoTelefono = (string?)null, ContactoCorreo = "accesos@planta.example" });
        visitas[_visitaCancelada].Should().BeEquivalentTo(new { ContactoNombre = (string?)null, ContactoTelefono = (string?)null, ContactoCorreo = (string?)null },
            "una Visita cancelada no tiene entrada que preparar");
        visitas[_visitaDeCentroSinContacto].Should().BeEquivalentTo(new { ContactoNombre = (string?)null, ContactoTelefono = (string?)null, ContactoCorreo = (string?)null });
    }

    [Fact]
    public async Task El_criterio_de_la_migracion_es_el_mismo_que_el_del_dominio()
    {
        await using (var contexto = NuevoContexto(new TenantPorAmbito()))
            await contexto.GetService<IMigrator>().MigrateAsync();

        await using var lectura = NuevoContexto(new TenantPorAmbito());
        var centros = await lectura.Centros.IgnoreQueryFilters().Select(c => new { c.Id, c.Contacto }).ToListAsync();
        var visitas = await lectura.Visitas.IgnoreQueryFilters().Where(v => !v.EstaCancelada).ToListAsync();

        visitas.Should().NotBeEmpty();
        foreach (var visita in visitas)
        {
            var esperada = new Visita(visita.CentroId, visita.FechaInicio, visita.FechaFin, null);
            esperada.RellenarContactoDesdeCentro(centros.Single(c => c.Id == visita.CentroId).Contacto);
            visita.Should().BeEquivalentTo(
                new { esperada.ContactoNombre, esperada.ContactoTelefono, esperada.ContactoCorreo },
                o => o.ExcludingMissingMembers());
        }
    }

    private static Visita NuevaVisita(Centro centro) => new(centro.Id, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3), null);

    private CaeManagerDbContext NuevoContexto(ITenantActual tenant)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenant))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class TenantPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
