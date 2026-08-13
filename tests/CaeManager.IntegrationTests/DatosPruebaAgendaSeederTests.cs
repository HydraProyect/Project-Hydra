using CaeManager.Domain.Contactos;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Requerimiento global nº 1 sobre la agenda de contactos: la siembra tiene
/// que dejar ejercitables todas las variantes del flujo "a quién se le pide
/// cada documento" — reparto por tipo, contacto único predeterminado, los
/// propósitos que no son documento, la precedencia del Centro sobre el
/// Cliente, agenda en Empresa y Subcontrata, y el caso de perfil incompleto
/// (un Cliente sin ningún contacto, cuya reclamación debe fallar con aviso).
///
/// Corre sobre base limpia porque la siembra real solo actúa en tenants
/// vírgenes (guard de "ya hay Clientes").
/// </summary>
public class DatosPruebaAgendaSeederTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_siembra_cubre_todas_las_variantes_de_la_agenda()
    {
        await using (var dbContext = CrearContexto())
        {
            var resumen = await DatosPruebaSeeder.SembrarSoloDatosAsync(dbContext, NullLogger.Instance);
            resumen.Should().NotBeNull("la base de datos está vacía, la siembra no debe omitirse");
        }

        await using var contexto = CrearContexto();

        var contactos = await contexto.ContactosAgenda.ToListAsync();
        var vinculos = await contexto.ContactosAgendaTiposDocumento.ToListAsync();

        contactos.Should().NotBeEmpty();

        // Las cuatro agendas existen: "todos deben tener contacto".
        contactos.Should().Contain(c => c.ClienteId != null);
        contactos.Should().Contain(c => c.EmpresaId != null);
        contactos.Should().Contain(c => c.SubcontrataId != null);
        contactos.Should().Contain(c => c.CentroId != null);

        // Reparto por tipo: al menos un Cliente con dos contactos que reciben
        // documentación distinta — el caso que motivó la agenda.
        var contactosConTipos = contactos.Where(c => vinculos.Any(v => v.ContactoAgendaId == c.Id)).ToList();
        contactosConTipos
            .Where(c => c.ClienteId != null)
            .GroupBy(c => c.ClienteId!.Value)
            .Should().Contain(g => g.Count() >= 2);

        // Predeterminado, teléfono para WhatsApp y los dos propósitos que no
        // son un TipoDocumento.
        contactos.Should().Contain(c => c.EsPredeterminado);
        contactos.Should().Contain(c => c.Telefono != null);
        contactos.Should().Contain(c => c.RecibeProgramacionVisitas);
        contactos.Should().Contain(c => c.RecibeFacturacion);

        // Precedencia Centro → Cliente: un contacto de Centro con un tipo que
        // también tiene alguien del Cliente al que pertenece ese Centro.
        var centros = await contexto.Centros.ToListAsync();
        var contactosDeCentro = contactos.Where(c => c.CentroId != null).ToList();
        contactosDeCentro.Should().NotBeEmpty();

        var solapa = contactosDeCentro.Any(contactoCentro =>
        {
            var clienteDelCentro = centros.First(ce => ce.Id == contactoCentro.CentroId).ClienteId;
            var tiposDelCentro = vinculos.Where(v => v.ContactoAgendaId == contactoCentro.Id).Select(v => v.TipoDocumentoId).ToHashSet();
            var idsDelCliente = contactos.Where(c => c.ClienteId == clienteDelCentro).Select(c => c.Id).ToHashSet();

            return vinculos.Any(v => idsDelCliente.Contains(v.ContactoAgendaId) && tiposDelCentro.Contains(v.TipoDocumentoId));
        });

        solapa.Should().BeTrue("hace falta un tipo que tengan a la vez el Centro y su Cliente para poder ver que gana el del Centro");

        // Perfil incompleto: un Cliente a propósito sin ningún contacto.
        var clienteIds = await contexto.Clientes.Select(c => c.Id).ToListAsync();
        var clientesConAgenda = contactos.Where(c => c.ClienteId != null).Select(c => c.ClienteId!.Value).ToHashSet();
        clienteIds.Should().Contain(id => !clientesConAgenda.Contains(id),
            "sin un cliente sin agenda no se puede ejercitar el aviso de que no hay a quién reclamar");
    }

    private CaeManagerDbContext CrearContexto()
    {
        // Tenant #1: el único con catálogo de TipoDocumento sembrado por
        // HasData, que la siembra de datos operativos necesita.
        var tenantActual = new TenantActualAmbiental { TenantId = TenantSeedData.IdPorDefecto };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
