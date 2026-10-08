using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <b>Los datos de la maqueta de las páginas 360 no pueden perder una fila ni cambiar de estado sin que un test se
/// ponga rojo.</b>
///
/// <para>
/// Se siembra en el orden real del arranque —<see cref="DelegacionDemoSeeder"/>, la matriz de la demo a dirección y
/// <see cref="Fichas360DemoSeeder"/>— sobre PostgreSQL real bajo RLS efectiva (<see cref="ArnesDeArranqueRuntime"/>), y
/// todo se lee con el contexto de tráfico normal dentro del ámbito del Tenant propietario Pizza Planet: lo que aquí se ve
/// es lo que vería la ficha. Los estados por Centro se miden con el servicio que da los pares exigidos
/// (<see cref="CalculoEstadoCentroService.ObtenerParesExigidosAsync"/>) y con la regla de tolerancia de las vistas con
/// contexto de Centro (<see cref="VigenciaEnCentro"/>), no con la tabla del sembrador.
/// </para>
///
/// <para>
/// Las esperas están escritas a mano, leídas de la maqueta, y son relativas a hoy: la maqueta está dibujada el
/// 08/10/2026 y cada fecha suya se siembra con el mismo desplazamiento respecto al día de negocio.
/// </para>
/// </summary>
public sealed class Fichas360DemoFixture : IAsyncLifetime
{
    internal ArnesDeArranqueRuntime Arnes { get; private set; } = null!;
    internal IConfiguration Configuracion { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        Configuracion = new ConfigurationBuilder()
            .AddConfiguration(Arnes.Servicios.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EscenariosDireccionDemoSeeder.ClaveConfiguracion] = "true",
                [Fichas360DemoSeeder.ClaveConfiguracion] = "true"
            })
            .Build();

        await SembrarAsync();
    }

    /// <summary>La siembra de demo en el orden de <c>Program.cs</c>; se puede repetir, como en cada arranque.</summary>
    internal async Task SembrarAsync()
    {
        using var ambito = Arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        var contexto = sp.GetRequiredService<CaeManagerDbContext>();
        var usuarios = sp.GetRequiredService<UserManager<ApplicationUser>>();

        await DelegacionDemoSeeder.SeedAsync(
            contexto, usuarios, sp.GetRequiredService<IUserStore<ApplicationUser>>(),
            Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        await EscenariosDireccionDemoSeeder.SeedAsync(contexto, usuarios, Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        await Fichas360DemoSeeder.SeedAsync(contexto, Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
    }

    public Task DisposeAsync() => Arnes.DisposeAsync().AsTask();
}

public class Fichas360DemoTests(Fichas360DemoFixture fixture) : IClassFixture<Fichas360DemoFixture>
{
    private const string TenantPropietario = "Pizza Planet S.L.";
    private const string Epi = "Entrega de EPI";
    private const string Aptitud = "Certificado de aptitud médica";

    private static readonly string[] CentrosDeLaMaqueta = ["Sede Sevilla", "Planta Bilbao", "Planta Murcia", "Almacén Vigo"];

    [Fact]
    public async Task Lo_sembrado_pertenece_al_Tenant_propietario_Pizza_Planet_y_a_ningun_otro()
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var tenantId = await bootstrap.Tenants.Where(t => t.Nombre == TenantPropietario).Select(t => t.Id).SingleAsync();

        string[] matriculas = ["9012 GHI", "7788 LLK", "4455 MNP", "1234 ABC"];
        (await bootstrap.Vehiculos.IgnoreQueryFilters().Where(v => matriculas.Contains(v.NumeroPlaca)).Select(v => v.TenantId).ToListAsync())
            .Should().HaveCount(4).And.OnlyContain(id => id == tenantId, "MEDIDO sin filtro de Tenant: los cuatro Vehículos son de Pizza Planet");

        string[] proyectos = ["Reforma nave Sevilla", "Mantenimiento Almacén Vigo"];
        (await bootstrap.Proyectos.IgnoreQueryFilters().Where(p => proyectos.Contains(p.Nombre)).Select(p => p.TenantId).ToListAsync())
            .Should().HaveCount(2).And.OnlyContain(id => id == tenantId);

        string[] identificaciones = ["60005208W", "60005210G", "60005211M", "X1005212C", "60005020K", "60005021E"];
        (await bootstrap.Trabajadores.IgnoreQueryFilters().Where(t => identificaciones.Contains(t.Dni!)).Select(t => t.TenantId).ToListAsync())
            .Should().HaveCount(6).And.OnlyContain(id => id == tenantId);

        string[] tiposDeProyecto = ["Acta de coordinación", "Apertura de centro de trabajo", "Plan de seguridad y salud"];
        (await bootstrap.TiposDocumento.IgnoreQueryFilters()
                .Where(t => tiposDeProyecto.Contains(t.Nombre))
                .Select(t => new { t.TenantId, t.AmbitoAplicacion })
                .ToListAsync())
            .Should().HaveCount(3).And.OnlyContain(t => t.TenantId == tenantId && t.AmbitoAplicacion == AmbitoAplicacion.Proyecto);
    }

    [Fact]
    public async Task La_Entrega_de_EPI_esta_en_cada_Centro_en_el_estado_de_la_maqueta()
    {
        var estados = await EstadosPorCentroAsync(Epi);

        estados.Should().BeEquivalentTo(new Dictionary<(string Trabajador, string Centro), EstadoDocumento>
        {
            // Sede Sevilla: Paula Campos Lara no tiene el documento («Pendiente») y a Óscar Bravo Nieto le falta la fecha.
            [("Paula Campos Lara", "Sede Sevilla")] = EstadoDocumento.Faltante,
            [("Óscar Bravo Nieto", "Sede Sevilla")] = EstadoDocumento.SinConfirmar,
            [("Noelia Lozano Marín", "Sede Sevilla")] = EstadoDocumento.Vigente,
            [("Héctor Pastor Rey", "Sede Sevilla")] = EstadoDocumento.Vigente,
            [("Óscar Ferrer Pons", "Sede Sevilla")] = EstadoDocumento.Vigente,

            // Almacén Vigo: Pedro venció el 01/09 (fuera de los 8 días), Sonia el 05/10 (dentro) y a Carla le caduca en 8 días.
            [("Pedro Gil Mora", "Almacén Vigo")] = EstadoDocumento.Vencido,
            [("Sonia Cano Prieto", "Almacén Vigo")] = EstadoDocumento.EnTolerancia,
            [("Carla Molina Ríos", "Almacén Vigo")] = EstadoDocumento.Urgente,
            [("Mateo Soler Vidal", "Almacén Vigo")] = EstadoDocumento.Vigente,
            [("Óscar Ferrer Pons", "Almacén Vigo")] = EstadoDocumento.Vigente,
            [("Andrés Mora Vila", "Almacén Vigo")] = EstadoDocumento.Vigente,
            [("Rubén Pastor Rey", "Almacén Vigo")] = EstadoDocumento.Vigente,
            [("Paula Salas Duque", "Almacén Vigo")] = EstadoDocumento.Vigente,

            [("Héctor Bravo Nieto", "Planta Murcia")] = EstadoDocumento.SinConfirmar,
            [("Irene Navarro Gil", "Planta Murcia")] = EstadoDocumento.Vigente,
            [("Óscar Ferrer Pons", "Planta Murcia")] = EstadoDocumento.Vigente,

            // Planta Bilbao: por fecha del documento todos vigentes; la periodicidad de seis meses se mide aparte.
            [("Mateo Soler Vidal", "Planta Bilbao")] = EstadoDocumento.Vigente,
            [("Óscar Ferrer Pons", "Planta Bilbao")] = EstadoDocumento.Vigente,
            [("Lucía Prats Roca", "Planta Bilbao")] = EstadoDocumento.Vigente,
            [("Nuria Sanz Coll", "Planta Bilbao")] = EstadoDocumento.Vigente,
            [("Carla Navarro Gil", "Planta Bilbao")] = EstadoDocumento.Vigente,
        }, "MEDIDO con los pares exigidos y la tolerancia por Centro: una fila por Trabajador y Centro que se lo exige, ni una más");
    }

    [Fact]
    public async Task La_aptitud_medica_esta_en_cada_Centro_en_el_estado_de_la_maqueta()
    {
        var estados = await EstadosPorCentroAsync(Aptitud);

        estados.Should().HaveCount(21, "MEDIDO: los mismos 21 pares Trabajador-Centro que la Entrega de EPI");
        estados.Where(par => par.Value != EstadoDocumento.Vigente).Should().BeEquivalentTo(
            new Dictionary<(string Trabajador, string Centro), EstadoDocumento>
            {
                [("Paula Campos Lara", "Sede Sevilla")] = EstadoDocumento.Vencido,
                [("Sonia Cano Prieto", "Almacén Vigo")] = EstadoDocumento.Vencido,
                [("Lucía Prats Roca", "Planta Bilbao")] = EstadoDocumento.Urgente,
                [("Héctor Bravo Nieto", "Planta Murcia")] = EstadoDocumento.SinConfirmar,
                [("Pedro Gil Mora", "Almacén Vigo")] = EstadoDocumento.SinConfirmar,
            },
            "MEDIDO: dos vencidas sin tolerancia, una que caduca en 12 días y dos sin fecha; el resto, vigentes");
    }

    [Fact]
    public async Task Los_requisitos_por_Centro_son_los_de_la_maqueta_tolerancia_periodicidad_y_bloqueo()
    {
        var medido = await MedirAsync(async contexto =>
        {
            var hoy = DiaDeNegocio.Hoy();
            var centros = (await contexto.Centros.ToListAsync()).ToDictionary(c => c.Nombre);
            var tipos = await contexto.TiposDocumento.Where(t => t.Nombre == Epi || t.Nombre == Aptitud).ToDictionaryAsync(t => t.Nombre, t => t.Id);
            var filas = (await contexto.TiposDocumentoCentros.ToListAsync()).ToDictionary(f => (f.TipoDocumentoId, f.CentroId));
            var tolerancias = await contexto.ToleranciasDocumentoClienteEmpresarial.ToListAsync();
            var trabajadores = (await contexto.Trabajadores.ToListAsync()).ToDictionary(t => t.NombreCompleto, t => t.Id);
            var documentos = await contexto.Documentos.Operativos().Where(d => d.TrabajadorId != null).ToListAsync();

            CondicionesDeAccesoDelCentro Condiciones(string tipo, string centro) => VigenciaEnCentro.Condiciones(
                filas.GetValueOrDefault((tipos[tipo], centros[centro].Id)),
                tolerancias.SingleOrDefault(t => t.TipoDocumentoId == tipos[tipo] && t.ClienteEmpresarialId == centros[centro].ClienteId)?.ToleranciaDias);

            ResultadoDeRequisito Requisito(string trabajador, string tipo, string centro) => ReglaBloqueoDeAcceso.Evaluar(
                documentos.Where(d => d.TrabajadorId == trabajadores[trabajador] && d.TipoDocumentoId == tipos[tipo])
                    .Select(d => new DocumentoParaAcceso(d.Vigencia, d.FechaEmision)),
                Condiciones(tipo, centro), hoy);

            return new
            {
                Hoy = hoy,
                Bloqueantes = CentrosDeLaMaqueta
                    .SelectMany(c => new[] { Epi, Aptitud }.Select(t => (Tipo: t, Centro: c)))
                    .Where(par => filas.GetValueOrDefault((tipos[par.Tipo], centros[par.Centro].Id)) is { Incluido: true, BloqueaAcceso: true })
                    .ToList(),
                ToleranciaEpiVigo = Condiciones(Epi, "Almacén Vigo").ToleranciaDias,
                ToleranciaAptitudVigo = Condiciones(Aptitud, "Almacén Vigo").ToleranciaDias,
                EpiDeSonia = Requisito("Sonia Cano Prieto", Epi, "Almacén Vigo"),
                EpiDeMateoEnBilbao = Requisito("Mateo Soler Vidal", Epi, "Planta Bilbao"),
                EpiDeMateoEnVigo = Requisito("Mateo Soler Vidal", Epi, "Almacén Vigo"),
                EpiDePaula = Requisito("Paula Campos Lara", Epi, "Sede Sevilla"),
                AptitudDePaula = Requisito("Paula Campos Lara", Aptitud, "Sede Sevilla"),
                AptitudDeNoelia = Requisito("Noelia Lozano Marín", Aptitud, "Sede Sevilla"),
            };
        });

        medido.Bloqueantes.Should().BeEquivalentTo(
            new[]
            {
                (Epi, "Sede Sevilla"), (Aptitud, "Sede Sevilla"), (Aptitud, "Planta Bilbao"), (Aptitud, "Planta Murcia"), (Aptitud, "Almacén Vigo")
            },
            "MEDIDO: la aptitud bloquea en los cuatro Centros y la Entrega de EPI solo en Sede Sevilla");

        medido.ToleranciaEpiVigo.Should().Be(8, "MEDIDO: Almacén Vigo no dice nada y hereda los 8 días de Cyberdyne Ibérica S.A.");
        medido.ToleranciaAptitudVigo.Should().Be(0, "MEDIDO: la aptitud no tiene tolerancia en ningún Centro");
        medido.EpiDeSonia.EnToleranciaHasta.Should().Be(medido.Hoy.AddDays(5), "MEDIDO: venció hace 3 días y vale 8 más («en tolerancia hasta 13/10»)");

        medido.EpiDeMateoEnBilbao.Should().Be(
            new ResultadoDeRequisito(SituacionDeRequisitoBloqueante.Vencido, medido.Hoy.AddDays(-220).AddMonths(6), null),
            "MEDIDO con la regla de acceso: se emitió hace 220 días (02/03), Planta Bilbao lo renueva cada 6 meses y allí ya venció, aunque el documento sea vigente");
        medido.EpiDeMateoEnVigo.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Cumplido, "MEDIDO: el mismo documento vale en Almacén Vigo");

        medido.EpiDePaula.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Ausente, "MEDIDO: Paula Campos Lara está bloqueada en Sede Sevilla por un documento que no hay");
        medido.AptitudDePaula.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Vencido, "MEDIDO: y por la aptitud vencida");
        medido.AptitudDeNoelia.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Cumplido, "MEDIDO: control — quien la tiene al día no está bloqueada");
    }

    [Fact]
    public async Task La_subcontrata_tiene_su_plantilla_sus_Centros_sus_verificaciones_y_su_documentacion_de_empresa()
    {
        var medido = await MedirAsync(async contexto =>
        {
            var (hoy, parametros) = (DiaDeNegocio.Hoy(), await contexto.ParametrosSistema.SingleAsync());
            var subcontrata = await contexto.Empresas.SingleAsync(e => e.RazonSocial == "Transportes Terminator S.L.");
            var skynet = await contexto.Empresas.SingleAsync(e => e.RazonSocial == "Montajes Skynet S.L.");
            var centros = await contexto.Centros.ToDictionaryAsync(c => c.Id, c => c.Nombre);
            var tipos = await contexto.TiposDocumento.ToDictionaryAsync(t => t.Id, t => t.Nombre);
            var trabajadores = await contexto.Trabajadores.ToListAsync();
            var asignaciones = await contexto.Asignaciones.Where(a => a.FechaBaja == null).ToListAsync();

            Dictionary<string, int> PlantillaPorCentro(Func<Trabajador, bool> deLaEmpresa) => asignaciones
                .Where(a => trabajadores.Any(t => t.Id == a.TrabajadorId && deLaEmpresa(t)))
                .GroupBy(a => centros[a.CentroId])
                .ToDictionary(g => g.Key, g => g.Count());

            return new
            {
                subcontrata.NivelServicio,
                Plantilla = trabajadores.Where(t => t.SubcontrataId == subcontrata.Id).Select(t => t.NombreCompleto).ToList(),
                CentrosDeLaSubcontrata = PlantillaPorCentro(t => t.SubcontrataId == subcontrata.Id),
                CentrosDeSkynet = PlantillaPorCentro(t => t.EmpresaId == skynet.Id),
                Verificaciones = (await contexto.VerificacionesExternaSubcontrata.Where(v => v.SubcontrataId == subcontrata.Id).ToListAsync())
                    .Select(v => (Tipo: tipos[v.TipoDocumentoId], Centro: centros[v.CentroId], v.Resultado, DiasDesdeHoy: v.FechaVerificacion.DayNumber - hoy.DayNumber))
                    .ToList(),
                DocumentosDeEmpresa = (await contexto.Documentos.Operativos().Where(d => d.EmpresaId == subcontrata.Id).ToListAsync())
                    .ToDictionary(d => tipos[d.TipoDocumentoId], d => d.CalcularEstado(hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)),
            };
        });

        medido.NivelServicio.Should().Be(nameof(NivelServicioSubcontrata.Supervisada));
        medido.Plantilla.Should().BeEquivalentTo(
            new[] { "Sonia Cano Prieto", "Pedro Gil Mora", "Mateo Soler Vidal", "Lucía Prats Roca", "Andrés Mora Vila", "Nuria Sanz Coll" },
            "MEDIDO: los seis Trabajadores de la pestaña de la maqueta");
        medido.CentrosDeLaSubcontrata.Should().BeEquivalentTo(
            new Dictionary<string, int> { ["Almacén Vigo"] = 4, ["Planta Bilbao"] = 3 },
            "MEDIDO: cuatro en Almacén Vigo; en Planta Bilbao son tres y no los dos de la maqueta, porque la página de Tipo de documento sitúa allí también a Mateo Soler Vidal");
        medido.CentrosDeSkynet.Should().BeEquivalentTo(
            new Dictionary<string, int> { ["Almacén Vigo"] = 4, ["Planta Murcia"] = 3, ["Sede Sevilla"] = 1, ["Planta Bilbao"] = 1 },
            "MEDIDO: los «Centros de su empleador» del Vehículo (4 y 3), más los dos de Umbrella Corporation Ibérica S.A. en los que la página de Tipo de documento sitúa a Óscar Ferrer Pons");

        medido.Verificaciones.Should().BeEquivalentTo(
            new[]
            {
                ("RNT", "Almacén Vigo", ResultadoVerificacionExterna.NoValido, -6),
                ("Certificado de estar al corriente con la Seguridad Social", "Almacén Vigo", ResultadoVerificacionExterna.Valido, -36),
                ("Evaluación de Riesgos Laborales", "Almacén Vigo", ResultadoVerificacionExterna.Valido, -36),
                ("Seguro de Responsabilidad Civil + recibo de pago", "Almacén Vigo", ResultadoVerificacionExterna.Valido, -36),
                ("Seguro de Responsabilidad Civil + recibo de pago", "Planta Bilbao", ResultadoVerificacionExterna.Valido, -36),
                ("Certificado de aptitud médica", "Planta Bilbao", ResultadoVerificacionExterna.NoValido, -23),
                ("Certificado de aptitud médica", "Almacén Vigo", ResultadoVerificacionExterna.Valido, -36),
            });

        medido.DocumentosDeEmpresa.Should().BeEquivalentTo(
            new Dictionary<string, EstadoDocumento>
            {
                ["RNT"] = EstadoDocumento.Vencido,
                ["Certificado de estar al corriente con la Seguridad Social"] = EstadoDocumento.Urgente,
                ["Evaluación de Riesgos Laborales"] = EstadoDocumento.SinConfirmar,
                ["Certificado de estar al corriente con Hacienda"] = EstadoDocumento.Vigente,
                ["RLC"] = EstadoDocumento.Vigente,
                ["Servicio de Prevención Ajeno"] = EstadoDocumento.Vigente,
                ["Seguro de Responsabilidad Civil + recibo de pago"] = EstadoDocumento.Vigente,
            },
            "MEDIDO: siete documentos y ninguna Planificación de la Actividad Preventiva, que en la maqueta «se exige y no hay documento»");
    }

    [Fact]
    public async Task El_Camion_grua_tiene_sus_cuatro_documentos_en_el_estado_de_la_maqueta_y_la_inspeccion_anterior_sustituida()
    {
        var medido = await MedirAsync(async contexto =>
        {
            var (hoy, parametros) = (DiaDeNegocio.Hoy(), await contexto.ParametrosSistema.SingleAsync());
            var vehiculo = await contexto.Vehiculos.SingleAsync(v => v.NumeroPlaca == "9012 GHI");
            var tipos = await contexto.TiposDocumento.ToDictionaryAsync(t => t.Id, t => t.Nombre);
            var documentos = await contexto.Documentos.Where(d => d.VehiculoId == vehiculo.Id).ToListAsync();

            return new
            {
                vehiculo.Nombre,
                vehiculo.Modelo,
                Empleador = await contexto.Empresas.Where(e => e.Id == vehiculo.EmpresaId).Select(e => e.RazonSocial).SingleAsync(),
                Operativos = documentos.Where(d => !d.EstaSustituido)
                    .ToDictionary(d => tipos[d.TipoDocumentoId], d => d.CalcularEstado(hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)),
                Sustituidos = documentos.Where(d => d.EstaSustituido).Select(d => tipos[d.TipoDocumentoId]).ToList(),
                VehiculosDeLaSubcontrata = await contexto.Vehiculos
                    .Where(v => contexto.Empresas.Any(e => e.Id == v.SubcontrataId && e.RazonSocial == "Transportes Terminator S.L."))
                    .Select(v => v.Nombre).ToListAsync(),
            };
        });

        (medido.Nombre, medido.Modelo, medido.Empleador).Should().Be(("Camión grúa", "Iveco Daily", "Montajes Skynet S.L."));
        medido.Operativos.Should().BeEquivalentTo(
            new Dictionary<string, EstadoDocumento>
            {
                ["ITC"] = EstadoDocumento.Vencido,
                ["Seguro"] = EstadoDocumento.Urgente,
                ["Ficha técnica"] = EstadoDocumento.SinConfirmar,
                ["Autorización de circulación"] = EstadoDocumento.SinCaducidad,
            },
            "MEDIDO: inspección vencida hace 36 días, seguro que caduca en 11, ficha sin fecha y permiso que no caduca");
        medido.Sustituidos.Should().BeEquivalentTo(new[] { "ITC" }, "MEDIDO: la inspección anterior queda en el historial, sustituida por la vigente");
        medido.VehiculosDeLaSubcontrata.Should().BeEquivalentTo(new[] { "Furgoneta T2", "Camión 7" });
    }

    [Fact]
    public async Task El_proyecto_Reforma_nave_Sevilla_tiene_sus_tecnicos_sus_documentos_y_las_dos_Visitas_de_su_Centro()
    {
        var medido = await MedirAsync(async contexto =>
        {
            var (hoy, parametros) = (DiaDeNegocio.Hoy(), await contexto.ParametrosSistema.SingleAsync());
            var proyecto = await contexto.Proyectos.SingleAsync(p => p.Nombre == "Reforma nave Sevilla");
            var centro = await contexto.Centros.SingleAsync(c => c.Id == proyecto.CentroId);
            var tipos = await contexto.TiposDocumento.ToDictionaryAsync(t => t.Id, t => t.Nombre);
            var trabajadores = (await contexto.Trabajadores.ToListAsync()).ToDictionary(t => t.Id, t => t.NombreCompleto);
            var visitas = await contexto.Visitas.Where(v => v.CentroId == centro.Id).OrderBy(v => v.FechaInicio).ToListAsync();
            var asistentes = await contexto.VisitasTrabajadores.ToListAsync();

            return new
            {
                Hoy = hoy,
                Centro = centro.Nombre,
                ClienteEmpresarial = await contexto.Empresas.Where(e => e.Id == proyecto.ClienteId).Select(e => e.RazonSocial).SingleAsync(),
                proyecto.FechaInicio,
                proyecto.FechaFinPrevista,
                proyecto.EstaAbierto,
                Tecnicos = (await contexto.ProyectosTecnicos.Where(t => t.ProyectoId == proyecto.Id).ToListAsync())
                    .ToDictionary(t => trabajadores[t.TrabajadorId], t => t.FechaBaja is null),
                Documentos = (await contexto.Documentos.Operativos().Where(d => d.ProyectoId == proyecto.Id).ToListAsync())
                    .ToDictionary(d => tipos[d.TipoDocumentoId], d => d.CalcularEstado(hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)),
                Visitas = visitas
                    .Select(v => (v.FechaInicio, v.FechaFin, v.HoraEstimadaAcceso, v.Origen,
                        Asistentes: string.Join(" y ", asistentes.Where(a => a.VisitaId == v.Id).Select(a => trabajadores[a.TrabajadorId]).Order(StringComparer.Ordinal))))
                    .ToList(),
                CanalPrincipal = await contexto.CanalesGestionDocumental
                    .Where(c => c.CentroId == centro.Id && c.EsPrincipal)
                    .Select(c => new { c.Tipo, c.EmailsDestinatarios })
                    .SingleAsync(),
            };
        });

        (medido.Centro, medido.ClienteEmpresarial).Should().Be(("Sede Sevilla", "Umbrella Corporation Ibérica S.A."));
        medido.FechaInicio.Should().Be(medido.Hoy.AddDays(-210), "MEDIDO: «210 días abierto»");
        medido.FechaFinPrevista.Should().Be(medido.Hoy.AddDays(53), "MEDIDO: «quedan 53 días»");
        medido.EstaAbierto.Should().BeTrue();

        medido.Tecnicos.Should().BeEquivalentTo(
            new Dictionary<string, bool>
            {
                ["Paula Campos Lara"] = true,
                ["Lucía Prats Roca"] = true,
                ["Óscar Bravo Nieto"] = true,
                ["Noelia Lozano Marín"] = true,
                ["Héctor Pastor Rey"] = true,
                ["Iván Ruiz Sala"] = false,
            },
            "MEDIDO: cinco técnicos activos y uno de baja");

        medido.Documentos.Should().BeEquivalentTo(
            new Dictionary<string, EstadoDocumento>
            {
                ["Acta de coordinación"] = EstadoDocumento.Proximo,
                ["Apertura de centro de trabajo"] = EstadoDocumento.SinConfirmar,
                ["Plan de seguridad y salud"] = EstadoDocumento.SinCaducidad,
            });

        medido.Visitas.Should().Equal(
            new (DateOnly, DateOnly, TimeOnly?, OrigenVisita, string)[]
            {
                (medido.Hoy.AddDays(-35), medido.Hoy.AddDays(-35), null, OrigenVisita.Plataforma, "Noelia Lozano Marín"),
                (medido.Hoy.AddDays(-2), medido.Hoy, new TimeOnly(8, 0), OrigenVisita.Correo, "Paula Campos Lara y Óscar Bravo Nieto"),
            },
            "MEDIDO: la finalizada del 03/09 con un Trabajador y la que termina hoy con dos, entrada a las 08:00 y origen Correo");

        (medido.CanalPrincipal.Tipo, medido.CanalPrincipal.EmailsDestinatarios).Should().Be(
            (TipoCanalGestion.Email, "accesos.sevilla@umbrella-iberica.example"));
    }

    [Fact]
    public async Task Sembrar_otra_vez_como_en_cada_arranque_no_duplica_ni_deshace_nada()
    {
        var antes = await ContarAsync();

        await fixture.SembrarAsync();

        (await ContarAsync()).Should().BeEquivalentTo(antes, "MEDIDO sin filtro de borrado: la segunda siembra no escribe ni una fila en Pizza Planet");
        antes["Vehiculos"].Should().Be(4, "MEDIDO: control — el recuento ve lo sembrado");
        (await EstadosPorCentroAsync(Epi))[("Sonia Cano Prieto", "Almacén Vigo")].Should().Be(
            EstadoDocumento.EnTolerancia, "MEDIDO: la siembra de base, al repetirse, no devuelve los documentos corregidos a su vigencia de origen");
    }

    private async Task<Dictionary<string, int>> ContarAsync()
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var id = await bootstrap.Tenants.Where(t => t.Nombre == TenantPropietario).Select(t => t.Id).SingleAsync();

        return new Dictionary<string, int>
        {
            ["Empresas"] = await bootstrap.Empresas.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["Relaciones"] = await bootstrap.RelacionesEmpresariales.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["Trabajadores"] = await bootstrap.Trabajadores.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["Asignaciones"] = await bootstrap.Asignaciones.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["AsignacionesDeBaja"] = await bootstrap.Asignaciones.IgnoreQueryFilters().CountAsync(e => e.TenantId == id && e.FechaBaja != null),
            ["Documentos"] = await bootstrap.Documentos.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["DocumentosRetirados"] = await bootstrap.Documentos.IgnoreQueryFilters().CountAsync(e => e.TenantId == id && e.EstaEliminado),
            ["Acreditaciones"] = await bootstrap.AcreditacionesDocumentoPlataforma.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["TiposDocumento"] = await bootstrap.TiposDocumento.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["Requisitos"] = await bootstrap.TiposDocumentoCentros.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["Tolerancias"] = await bootstrap.ToleranciasDocumentoClienteEmpresarial.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["Canales"] = await bootstrap.CanalesGestionDocumental.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["Vehiculos"] = await bootstrap.Vehiculos.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["Proyectos"] = await bootstrap.Proyectos.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["Tecnicos"] = await bootstrap.ProyectosTecnicos.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["Visitas"] = await bootstrap.Visitas.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["Asistentes"] = await bootstrap.VisitasTrabajadores.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
            ["Verificaciones"] = await bootstrap.VerificacionesExternaSubcontrata.IgnoreQueryFilters().CountAsync(e => e.TenantId == id),
        };
    }

    /// <summary>
    /// El estado de un Tipo de documento para cada Trabajador en cada Centro de la maqueta que se lo exige: el par
    /// exigido que calcula la aplicación y, encima, la tolerancia del Centro (o la de su Cliente empresarial), que es lo
    /// que convierte un Vencido en «En tolerancia» en las vistas con contexto de Centro.
    /// </summary>
    private Task<Dictionary<(string Trabajador, string Centro), EstadoDocumento>> EstadosPorCentroAsync(string nombreTipo) =>
        MedirAsync(async contexto =>
        {
            var hoy = DiaDeNegocio.Hoy();
            var centros = await contexto.Centros.Where(c => CentrosDeLaMaqueta.Contains(c.Nombre)).ToListAsync();
            var tipoId = await contexto.TiposDocumento.Where(t => t.Nombre == nombreTipo).Select(t => t.Id).SingleAsync();
            var filas = await contexto.TiposDocumentoCentros.Where(f => f.TipoDocumentoId == tipoId).ToDictionaryAsync(f => f.CentroId);
            var tolerancias = await contexto.ToleranciasDocumentoClienteEmpresarial
                .Where(t => t.TipoDocumentoId == tipoId)
                .ToDictionaryAsync(t => t.ClienteEmpresarialId, t => (int?)t.ToleranciaDias);
            var trabajadores = (await contexto.Trabajadores.ToListAsync()).ToDictionary(t => t.Id, t => t.NombreCompleto);
            var documentos = (await contexto.Documentos.Operativos()
                    .Where(d => d.TrabajadorId != null && d.TipoDocumentoId == tipoId)
                    .ToListAsync())
                .ToDictionary(d => d.TrabajadorId!.Value);

            var servicio = new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto);
            var pares = await servicio.ObtenerParesExigidosAsync(centros.Select(c => c.Id).ToList(), CancellationToken.None);

            return pares.Where(par => par.TipoDocumentoId == tipoId).ToDictionary(
                par => (trabajadores[par.TrabajadorId], centros.Single(c => c.Id == par.CentroId).Nombre),
                par => documentos.TryGetValue(par.TrabajadorId, out var documento)
                    ? VigenciaEnCentro.Aplicar(
                        par.Estado, documento.EstadoVigencia, documento.FechaVencimiento, documento.FechaEmision,
                        VigenciaEnCentro.Condiciones(filas.GetValueOrDefault(par.CentroId), tolerancias.GetValueOrDefault(par.ClienteEmpresarialId)),
                        hoy).Estado
                    : par.Estado);
        });

    /// <summary>Lee con el contexto de tráfico normal, bajo RLS, dentro del ámbito del Tenant propietario Pizza Planet.</summary>
    private async Task<T> MedirAsync<T>(Func<CaeManagerDbContext, Task<T>> medir)
    {
        Guid tenantId;
        await using (var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear())
            tenantId = await bootstrap.Tenants.Where(t => t.Nombre == TenantPropietario).Select(t => t.Id).SingleAsync();

        using var ambito = fixture.Arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        using (AmbitoTenantExplicito.Establecer(tenantId))
            return await medir(contexto);
    }
}

/// <summary>La guarda de <see cref="Fichas360DemoSeeder"/>, sin base de datos: decide antes de leer o escribir nada.</summary>
public class Fichas360DemoArranqueTests
{
    [Theory]
    [InlineData("Production", true, true, true, "*Fichas360 no puede activarse en Producción*")]
    [InlineData("Development", true, false, true, "*Fichas360 exige DatosPrueba:EscenariosDireccion*")]
    [InlineData("Development", true, true, true, null)]
    [InlineData("Production", true, true, false, null)]
    [InlineData("Production", false, true, true, null)]
    public void La_guarda_rechaza_Produccion_y_la_clave_a_medias_y_es_inerte_sin_la_clave(
        string entorno, bool datosPrueba, bool escenarios, bool fichas, string? mensajeEsperado)
    {
        var llamada = () => Fichas360DemoSeeder.RechazarEnProduccion(Configuracion(datosPrueba, escenarios, fichas), new EntornoDePrueba(entorno));

        if (mensajeEsperado is null)
            llamada.Should().NotThrow("MEDIDO: inerte sin sus claves, y con las tres activas fuera de Producción no rechaza");
        else
            llamada.Should().Throw<InvalidOperationException>().WithMessage(mensajeEsperado);
    }

    [Fact]
    public async Task Sin_la_clave_no_toca_la_base_aunque_las_otras_dos_esten_activas()
    {
        // MEDIDO: sin contexto. Si la siembra leyera o escribiera algo con la clave apagada, lanzaría NullReferenceException.
        var siembra = () => Fichas360DemoSeeder.SeedAsync(
            null!, Configuracion(datosPrueba: true, escenarios: true, fichas: false), EntornoDePrueba.Desarrollo, NullLogger.Instance);

        await siembra.Should().NotThrowAsync();
    }

    [Fact]
    public void El_arranque_invoca_la_guarda_antes_de_cualquier_siembra_y_la_siembra_despues_de_la_rama_de_la_que_depende()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        var programa = File.ReadAllText(Path.Combine(directorio!.FullName, "src", "CaeManager.Web", "Program.cs"));

        int Posicion(string texto) => programa.IndexOf(texto, StringComparison.Ordinal);

        Posicion("Fichas360DemoSeeder.RechazarEnProduccion(").Should().BeGreaterThan(0, "MEDIDO en el fuente real de Program.cs: la guarda está cableada");
        Posicion("Fichas360DemoSeeder.RechazarEnProduccion(").Should().BeLessThan(Posicion("IdentitySeeder.SeedAsync("), "el rechazo es previo a toda escritura");
        Posicion("Fichas360DemoSeeder.SeedAsync(").Should().BeGreaterThan(
            Posicion("EscenariosDireccionDemoSeeder.SeedAsync("), "siembra encima de la rama de Pizza Planet: tiene que correr después");
    }

    private static IConfiguration Configuracion(bool datosPrueba, bool escenarios, bool fichas) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = datosPrueba.ToString(),
            [EscenariosDireccionDemoSeeder.ClaveConfiguracion] = escenarios.ToString(),
            [Fichas360DemoSeeder.ClaveConfiguracion] = fichas.ToString(),
        }).Build();
}
