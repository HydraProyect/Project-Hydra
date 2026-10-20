using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// La migración <c>FotoDelTrabajadorEnLaAsignacionAVisita</c> rellena la foto de las asignaciones a Visita que ya existen
/// en staging y producción con el criterio vigente (<see cref="CriterioExtranjeroCobrable.Efectivo"/>), y solo copia el
/// documento y el nombre de las cobrables (minimización). Las asignaciones se siembran con el esquema actual y sin foto,
/// se deshace la migración (que solo quita las columnas) y se vuelve a aplicar: el relleno es lo único que decide la foto.
/// </summary>
public class FotoDelTrabajadorEnLaAsignacionAVisitaTests : IAsyncLifetime
{
    private const string MigracionDelCambio = "20261020184209_FotoDelTrabajadorEnLaAsignacionAVisita";

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _tenant = new("Tenant de la foto");
    private readonly Dictionary<string, Guid> _trabajadores = [];

    public async Task InitializeAsync()
    {
        await using (var contexto = NuevoContexto())
            await contexto.Database.MigrateAsync();

        using (AmbitoTenantExplicito.Establecer(_tenant.Id))
        await using (var contexto = NuevoContexto())
        {
            contexto.Tenants.Add(_tenant);
            var cliente = Empresa.CrearComoCliente("Cliente de la foto", "B12345674", false, null, null);
            var empresa = new Empresa("Contrata de la foto");
            contexto.Empresas.AddRange(cliente, empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro de la foto");
            var hoy = DiaDeNegocio.Hoy();
            var visita = new Visita(centro.Id, hoy.AddDays(-20), hoy.AddDays(-19), null);
            contexto.Centros.Add(centro);
            contexto.Visitas.Add(visita);

            var porDni = Trabajador.DeEmpresa(empresa.Id, "Dani", "Dni", "12345678Z");
            var porNie = Trabajador.DeEmpresa(empresa.Id, "Nora", "Nie", "X1234567L");
            var porPasaporte = Trabajador.DeEmpresa(empresa.Id, "Pablo", "Pasaporte", "AB1234567");
            var pasaporteDesmarcado = Trabajador.DeEmpresa(empresa.Id, "Pia", "Desmarcada", "FR9876543");
            pasaporteDesmarcado.FijarMarcaExtranjeroCobrable(false);
            var nieMarcado = Trabajador.DeEmpresa(empresa.Id, "Yuri", "Marcado", "Y1234567X");
            nieMarcado.FijarMarcaExtranjeroCobrable(true);
            var marcadorHeredado = Trabajador.DeEmpresa(empresa.Id, "Sara", "Marcador", "ZZ0000001");
            var dniDeSieteDigitos = Trabajador.DeEmpresa(empresa.Id, "Sete", "Digitos", "ZZ0000002");
            var anonimizado = Trabajador.DeEmpresa(empresa.Id, "Ana", "Anonima", "CD7654321");

            Trabajador[] todos = [porDni, porNie, porPasaporte, pasaporteDesmarcado, nieMarcado, marcadorHeredado, dniDeSieteDigitos, anonimizado];
            foreach (var t in todos)
                _trabajadores[t.Nombre] = t.Id;
            anonimizado.Anonimizar(DateTime.UtcNow);
            contexto.Trabajadores.AddRange(todos);
            await contexto.SaveChangesAsync();

            // Sin foto: como las asignaciones anteriores a la migración.
            contexto.VisitasTrabajadores.AddRange(todos.Select(t => new VisitaTrabajador(visita.Id, t.Id)));
            await contexto.SaveChangesAsync();

            // Documentos heredados que la validación de hoy ya no admite en el alta.
            await contexto.Trabajadores.Where(t => t.Id == marcadorHeredado.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Dni, "SIN DNI"));
            await contexto.Trabajadores.Where(t => t.Id == dniDeSieteDigitos.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Dni, "1234567-L"));
        }

        await using (var contexto = NuevoContexto())
        {
            var migrador = contexto.GetService<IMigrator>();
            await migrador.MigrateAsync(MigracionAnterior(contexto));
            await migrador.MigrateAsync();
        }
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task El_relleno_aplica_el_criterio_vigente_a_cada_asignacion()
    {
        var fotos = await LeerFotosAsync();

        fotos["Dani"].Cobrable.Should().BeFalse("el DNI no cuenta");
        fotos["Nora"].Cobrable.Should().BeFalse("el NIE no cuenta");
        fotos["Pablo"].Cobrable.Should().BeTrue("el Pasaporte cuenta");
        fotos["Pia"].Cobrable.Should().BeFalse("la casilla desmarcada manda sobre el Pasaporte");
        fotos["Yuri"].Cobrable.Should().BeTrue("la casilla marcada manda sobre el NIE");
        fotos["Sara"].Cobrable.Should().BeFalse("un marcador heredado no es un documento");
        fotos["Sete"].Cobrable.Should().BeFalse("siete dígitos y letra es un DNI sin el cero inicial");
        fotos["Ana"].Cobrable.Should().BeFalse("sin documento no cuenta");

        foreach (var (nombre, foto) in fotos)
            foto.Cobrable.Should().Be(CriterioExtranjeroCobrable.Efectivo(foto.Marca, foto.Dni),
                $"el SQL del relleno es el mismo criterio que el código ({nombre})");
    }

    [Fact]
    public async Task Solo_las_asignaciones_cobrables_copian_documento_y_nombre()
    {
        var fotos = await LeerFotosAsync();

        fotos["Pablo"].Documento.Should().Be("AB1234567");
        fotos["Pablo"].Nombre.Should().Be("Pablo Pasaporte");
        fotos["Yuri"].Documento.Should().Be("Y1234567X");
        fotos.Where(f => !f.Value.Cobrable)
            .Should().OnlyContain(f => f.Value.Documento == null && f.Value.Nombre == null, "minimización");
    }

    [Fact]
    public async Task El_relleno_no_toca_la_RLS_de_las_tablas()
    {
        await using var contexto = NuevoContexto();
        var conexion = contexto.Database.GetDbConnection();
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT bool_and(relrowsecurity AND relforcerowsecurity)
            FROM pg_class WHERE relname IN ('VisitasTrabajadores', 'Trabajadores')
            """;

        (await comando.ExecuteScalarAsync()).Should().Be(true);
    }

    private async Task<Dictionary<string, (bool Cobrable, string? Documento, string? Nombre, bool? Marca, string? Dni)>> LeerFotosAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenant.Id);
        await using var contexto = NuevoContexto();
        var filas = await (
                from vt in contexto.VisitasTrabajadores
                join t in contexto.Trabajadores.IgnoreQueryFilters() on vt.TrabajadorId equals t.Id
                where t.TenantId == _tenant.Id
                select new { vt.TrabajadorId, vt.EsExtranjeroCobrable, vt.DocumentoIdentidad, vt.NombreMostrado, t.MarcaExtranjeroCobrable, t.Dni })
            .ToListAsync();

        filas.Should().HaveCount(_trabajadores.Count);
        return _trabajadores.ToDictionary(
            p => p.Key,
            p => filas.Where(f => f.TrabajadorId == p.Value)
                .Select(f => (f.EsExtranjeroCobrable, f.DocumentoIdentidad, f.NombreMostrado, f.MarcaExtranjeroCobrable, f.Dni))
                .Single());
    }

    private static string MigracionAnterior(CaeManagerDbContext contexto)
    {
        var migraciones = contexto.Database.GetMigrations().ToList();
        var indice = migraciones.IndexOf(MigracionDelCambio);
        indice.Should().BeGreaterThan(0, "la migración del cambio existe en el ensamblado y no es la línea base");
        return migraciones[indice - 1];
    }

    private CaeManagerDbContext NuevoContexto()
    {
        var tenantActual = new TenantActualPorAmbito();
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
