using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Dashboard;

/// <summary>
/// Las unidades de la visita de Trabajador extranjero que valora el Operador CAE externo (decisión de Chris,
/// 2026-10-20) contra PostgreSQL: los límites del mes son días civiles (el 1 y el 1 del mes siguiente), no la
/// medianoche de Madrid expresada en UTC, que cae el día anterior y desplazaría un día cada límite. Y, acotadas a
/// un tramo de vigencia de la Asignación de Operación, solo cuentan las Visitas que empiezan dentro del tramo.
/// </summary>
public class VisitasTrabajadorExtranjeroMedicionTests : IAsyncLifetime
{
    private const string NieValido = "X1234567L";
    private const string OtroNieValido = "Y1234567X";
    private const string DniValido = "77189989B";

    private static readonly MesCuadroDireccion Octubre = new(2025, 10);

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Cliente Visitas Extranjeros S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Contrata Visitas Extranjeros S.L.", "B87654323");
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Visitas Extranjeros");
        var recurrente = Trabajador.DeEmpresa(empresa.Id, "Amina", "Benali", NieValido);
        var otro = Trabajador.DeEmpresa(empresa.Id, "Ion", "Popescu", OtroNieValido);
        var nacional = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", DniValido);
        contexto.Centros.Add(centro);
        contexto.Trabajadores.AddRange(recurrente, otro, nacional);
        await contexto.SaveChangesAsync();

        // 30-sep y 1-nov quedan fuera de octubre; el 1 y el 31 dentro. El 31 lleva dos extranjeros, así que un
        // límite desplazado un día (30-sep dentro, 31-oct fuera) daría 2 en vez de 3.
        var dia30Sep = new Visita(centro.Id, new DateOnly(2025, 9, 30), new DateOnly(2025, 9, 30), null);
        var dia1 = new Visita(centro.Id, new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 1), null);
        var dia15 = new Visita(centro.Id, new DateOnly(2025, 10, 15), new DateOnly(2025, 10, 15), null);
        var dia31 = new Visita(centro.Id, new DateOnly(2025, 10, 31), new DateOnly(2025, 10, 31), null);
        var dia1Nov = new Visita(centro.Id, new DateOnly(2025, 11, 1), new DateOnly(2025, 11, 1), null);
        contexto.Visitas.AddRange(dia30Sep, dia1, dia15, dia31, dia1Nov);
        await contexto.SaveChangesAsync();

        contexto.VisitasTrabajadores.AddRange(
            new VisitaTrabajador(dia30Sep.Id, recurrente.Id),
            new VisitaTrabajador(dia1.Id, recurrente.Id),
            new VisitaTrabajador(dia15.Id, nacional.Id),
            new VisitaTrabajador(dia31.Id, recurrente.Id),
            new VisitaTrabajador(dia31.Id, otro.Id),
            new VisitaTrabajador(dia1Nov.Id, recurrente.Id));

        // Altas por tramo: el Centro se da de alta el 20-oct, una Obra el 5-oct y un Evento el 25-oct (no cuenta).
        var obra = Proyecto.Crear(cliente.Id, centro.Id, "Obra de octubre", new DateOnly(2025, 10, 5), null, null);
        var evento = Proyecto.Crear(cliente.Id, centro.Id, "Evento de octubre", new DateOnly(2025, 10, 25), new DateOnly(2025, 10, 26), null, TipoProyecto.Evento);
        contexto.Proyectos.AddRange(obra, evento);
        await contexto.SaveChangesAsync();
        await contexto.Centros.Where(c => c.Id == centro.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CreadoEnUtc, new DateTime(2025, 10, 20, 9, 0, 0, DateTimeKind.Utc)));
        await contexto.Proyectos.Where(p => p.Id == obra.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CreadoEnUtc, new DateTime(2025, 10, 5, 9, 0, 0, DateTimeKind.Utc)));
        await contexto.Proyectos.Where(p => p.Id == evento.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CreadoEnUtc, new DateTime(2025, 10, 25, 9, 0, 0, DateTimeKind.Utc)));
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Cuenta_las_Visitas_del_dia_1_y_del_ultimo_dia_del_mes_y_no_las_de_los_dias_vecinos()
    {
        await using var contexto = CrearContexto();

        var medida = await Medicion(contexto).MedirAsync(Octubre, DateTime.UtcNow.AddMinutes(1), CancellationToken.None, medirDocumental: false);

        medida.VisitasTrabajadorExtranjero.Should().Be(3,
            "1-oct (uno) y 31-oct (dos) cuentan; 30-sep y 1-nov no; el Trabajador con DNI tampoco");
    }

    [Fact]
    public async Task Acotadas_a_un_tramo_de_vigencia_solo_cuentan_las_Visitas_que_empiezan_dentro()
    {
        await using var contexto = CrearContexto();
        var medicion = Medicion(contexto);
        var corte = DateTime.UtcNow.AddMinutes(1);
        var mitad = new DateTime(2025, 10, 15, 10, 0, 0, DateTimeKind.Utc);

        var segundaMitad = await medicion.MedirUnidadesOperadorAsync([new TramoDeVigencia(mitad, Octubre.FinUtc)], corte, CancellationToken.None);
        var primeraMitad = await medicion.MedirUnidadesOperadorAsync([new TramoDeVigencia(Octubre.InicioUtc, mitad)], corte, CancellationToken.None);
        var mesEntero = await medicion.MedirUnidadesOperadorAsync([new TramoDeVigencia(Octubre.InicioUtc, Octubre.FinUtc)], corte, CancellationToken.None);

        segundaMitad.VisitasTrabajadorExtranjero.Should().Be(2, "solo la Visita del 31 cae en la segunda mitad");
        primeraMitad.VisitasTrabajadorExtranjero.Should().Be(1, "solo la Visita del 1 cae en la primera mitad");
        mesEntero.VisitasTrabajadorExtranjero.Should().Be(3, "el mes entero coincide con la medición del mes");

        segundaMitad.AltasCentros.Should().Be(1, "el Centro se dio de alta el 20");
        primeraMitad.AltasCentros.Should().Be(0);
        primeraMitad.ObrasNuevas.Should().Be(1, "la Obra se dio de alta el 5");
        segundaMitad.ObrasNuevas.Should().Be(0, "el Evento del 25 no es una Obra");
        mesEntero.Should().Be(new UnidadesOperadorCae(1, 1, 3));

        var medida = await medicion.MedirAsync(Octubre, corte, CancellationToken.None, medirDocumental: false);
        (medida.AltasCentros, medida.ObrasNuevas).Should().Be((1, 1), "control: el mes entero da lo mismo por las dos vías");
    }

    private MedicionMensualCuadroDireccion Medicion(CaeManagerDbContext contexto) =>
        new(new TenantActualAmbiental { TenantId = _tenant }, contexto, contexto, contexto, contexto, contexto, mediator: null!,
            proyectosContext: contexto, visitasContext: contexto);

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
