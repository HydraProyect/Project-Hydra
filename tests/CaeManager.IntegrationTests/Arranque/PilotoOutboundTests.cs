using System.IO.Compression;
using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Cumplimiento.Commands.ActivarTratamientoIaPorEncargo;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesSinRespuesta;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Application.Visitas.Queries.ObtenerPaqueteDocumentalVisita;
using CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf.IO;
using Xunit;
using Xunit.Abstractions;
using Informe = CaeManager.Infrastructure.Persistence.Seed.PilotoOutboundAutoverificacion.Informe;
using MedicionCentro = CaeManager.Infrastructure.Persistence.Seed.PilotoOutboundAutoverificacion.MedicionCentro;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// Las clases del piloto corren una detrás de otra: cada una crea y migra su base
/// y genera un centenar de PDF, uno de ellos pesado, y así cargan menos el clúster
/// compartido. En la primera ejecución, con las cinco en paralelo, el
/// <c>DROP DATABASE</c> del desmontaje de una agotó su tiempo de espera (causa no aislada).
/// </summary>
[CollectionDefinition(Nombre)]
public sealed class ColeccionPilotoOutbound
{
    public const string Nombre = "Piloto Outbound";
}

/// <summary>Una siembra del piloto, una vez, con el correo de contactos configurado.</summary>
public sealed class PilotoOutboundFixture : IAsyncLifetime
{
    internal ArnesPilotoOutbound Arnes { get; private set; } = null!;
    internal IConfiguration Configuracion { get; private set; } = null!;
    internal PilotoOutboundSeeder.Resultado Resultado { get; private set; } = null!;
    internal Informe Informe { get; private set; } = null!;
    internal ArnesPilotoOutbound.RegistroDeAvisos Registro { get; } = new();

    public async Task InitializeAsync()
    {
        Arnes = await ArnesPilotoOutbound.CrearAsync();
        Configuracion = ArnesPilotoOutbound.Configurar(
            ArnesPilotoOutbound.FechaDemostracion(),
            extra: (OpcionesPilotoOutbound.ClaveCorreoContactos, ArnesPilotoOutbound.CorreoDePrueba));

        Resultado = (await Arnes.SembrarAsync(Configuracion, logger: Registro))!;
        await Arnes.BackfillAsync();
        Informe = await Arnes.MedirAsync(Configuracion);
    }

    public async Task DisposeAsync() => await Arnes.DisposeAsync();
}

/// <summary>
/// La siembra del piloto del Servicio TALVEG Outbound, medida con las consultas
/// de las pantallas y la identidad de las cuentas sembradas, bajo el rol
/// restringido y con el almacén real. Los valores esperados están escritos a
/// mano aquí —no salen del catálogo ni del sembrador—: son los de la matriz.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class PilotoOutboundTests(PilotoOutboundFixture fixture, ITestOutputHelper salida) : IClassFixture<PilotoOutboundFixture>
{
    private static readonly TenantPilotoOutbound C = CatalogoPilotoOutbound.C;

    [Fact]
    public void La_siembra_escribio_y_la_autoverificacion_pasa()
    {
        fixture.Resultado.Escribio.Should().BeTrue();
        fixture.Resultado.TenantsConDatosNuevos.Should().Equal(
            CatalogoPilotoOutbound.EnOrdenDeSiembra.Select(t => t.Nombre), "MEDIDO: el autónomo primero y el perfil multicliente el último");
        fixture.Resultado.Pdf.Should().Be(fixture.Resultado.Documentos, "MEDIDO: un PDF por documento");
        fixture.Resultado.Documentos.Should().BeGreaterThan(500, "control: los once perfiles siembran documentos");

        // La duración del lote y la memoria pico las dice la propia siembra, en su línea de registro.
        var lineaDelLote = fixture.Registro.Informativas.Should()
            .ContainSingle(l => l.StartsWith("Siembra del piloto Outbound:", StringComparison.Ordinal)).Subject;
        salida.WriteLine($"MEDIDO {lineaDelLote}");
        lineaDelLote.Should().Contain("memoria pico del proceso");
        salida.WriteLine($"MEDIDO duración del lote según su resultado: {fixture.Resultado.Duracion.TotalSeconds:F1} s; avisos de la siembra: {fixture.Registro.Avisos.Count}");

        foreach (var m in fixture.Informe.Tenants)
            salida.WriteLine("MEDIDO " + PilotoOutboundTextos.Linea(m));

        // Lo que se lee de un vistazo antes de la demostración: los Centros de cada Tenant por estado, y si queda alguno en verde.
        foreach (var m in fixture.Informe.Tenants)
            salida.WriteLine($"MEDIDO {m.Clave}: {PilotoOutboundTextos.CentrosPorEstado(m)}");
        salida.WriteLine(
            "MEDIDO Centros «Vigente» en los Tenants del piloto: " +
            fixture.Informe.Tenants.Sum(m => m.Centros.Count(c => c.Estado == EstadoCentro.Vigente)));

        PilotoOutboundAutoverificacion.Discrepancias(fixture.Informe).Should().BeEmpty();
        fixture.Informe.Tenants.Should().HaveCount(CatalogoPilotoOutbound.Tenants.Count);
    }

    /// <summary>
    /// Lo que el Asistente IA comprueba antes de enviar nada al proveedor, con la identidad de las dos cuentas que
    /// lo usan en la demostración: el Tenant en pantalla y toda la cartera tienen instrucción de tratamiento de IA
    /// vigente. Se afirma por nombre contra el catálogo: una cartera vacía también «pasaría». Cada Gestor CAE tiene su
    /// cartera (sus exclusivos y el compartido); la Coordinadora CAE, la de los dos.
    /// </summary>
    [Fact]
    public async Task El_asistente_IA_no_falla_cerrado_para_la_Gestora_CAE_ni_para_la_Coordinadora_CAE()
    {
        var asistenteIa = fixture.Informe.AsistenteIa;
        asistenteIa.Should().NotBeNull("control: la autoverificación midió la comprobación de cartera");
        List<string> CarteraDe(Func<TenantPilotoOutbound, bool> suya) =>
            [.. CatalogoPilotoOutbound.Tenants.Where(suya).Select(t => t.Nombre), CatalogoPilotoOutbound.NombreTenantOperador];

        foreach (var (cuenta, cartera, esperada) in new[]
                 {
                     ("Gestora CAE", asistenteIa!.Gestora, CarteraDe(t => t.EnCarteraDeLaGestoraPrimera)),
                     ("Gestor CAE segundo", asistenteIa.GestorSegundo, CarteraDe(t => t.EnCarteraDelGestorSegundo)),
                     ("Coordinadora CAE", asistenteIa.Coordinadora, CarteraDe(_ => true))
                 })
        {
            salida.WriteLine($"MEDIDO cartera de la {cuenta}: con instrucción {cartera.ConInstruccion.Count}, sin instrucción {cartera.SinInstruccion.Count}");
            cartera.SinInstruccion.Should().BeEmpty($"MEDIDO: ningún Tenant de la cartera de la {cuenta} está sin instrucción");
            cartera.ConInstruccion.Should().BeEquivalentTo(
                esperada, $"MEDIDO: la cartera de la {cuenta} es la que el catálogo le da, toda con instrucción");
        }

        asistenteIa.Coordinadora.ConInstruccion.Should().HaveCount(CatalogoPilotoOutbound.Tenants.Count + 1, "control: el Operador CAE externo y los once Tenants propietarios");

        // Las dos condiciones que el modo Preguntar exige antes de llamar al proveedor, con la Gestora CAE dentro de T1.
        var (enPantalla, errorDeCartera) = await fixture.Arnes.ComoGestoraPrimeraEnAsync(C.Nombre, async sp =>
        {
            var tenantId = sp.GetRequiredService<ITenantActual>().TenantId;
            tenantId.Should().NotBeNull("control: dentro de T1 hay Tenant en pantalla");
            return (
                await sp.GetRequiredService<IInstruccionTratamientoIaService>().EstaHabilitadaAsync(tenantId!.Value),
                (await sp.GetRequiredService<ISender>().Send(new ComprobarInstruccionIaCarteraQuery())).ErrorSiFalta());
        });
        enPantalla.Should().BeTrue("MEDIDO: T1, el Tenant en pantalla, tiene instrucción vigente");
        errorDeCartera.Should().BeNull("MEDIDO: la comprobación de cartera no devuelve el error que impide enviar el mensaje");
    }

    /// <summary>
    /// Los tres Tenants escaparate venden un estado documental concreto, medido en todos los ámbitos (Trabajador, Empresa
    /// propia, Subcontrata y Vehículo), no solo en lo que cuenta Inicio: el grande no tiene nada vencido ni que venza en
    /// treinta días; el de «por vencer», exactamente nueve documentos que vencen a D+4 y nada más en esos treinta días; el
    /// de «pocos vencidos», exactamente cinco vencidos y nada por vencer. Los números están escritos a mano.
    /// </summary>
    [Fact]
    public async Task Los_tres_Tenants_escaparate_tienen_el_estado_documental_que_venden()
    {
        var d = ArnesPilotoOutbound.FechaDemostracion();
        var medidos = new Dictionary<string, List<(string Ambito, DateOnly Vence)>>();
        foreach (var tenant in CatalogoPilotoOutbound.Escaparate)
        {
            medidos[tenant.Clave] = await fixture.Arnes.EnTenantAsync(await fixture.Arnes.TenantIdAsync(tenant.Nombre), async (db, _) =>
            {
                var propia = await db.Empresas.Where(e => e.EsPropia).Select(e => e.Id).SingleAsync();
                var razones = tenant.Subcontratas.Select(s => s.RazonSocial).ToList();
                var subcontratas = await db.Empresas.Where(e => razones.Contains(e.RazonSocial)).Select(e => e.Id).ToListAsync();
                subcontratas.Should().HaveCount(razones.Count, $"control: {tenant.Clave} tiene sus Subcontratas sembradas");

                var documentos = await db.Documentos.AsNoTracking()
                    .Where(x => x.SustituidoEnUtc == null && x.FechaVencimiento != null && x.FechaVencimiento <= d.AddDays(30))
                    .Select(x => new { x.TrabajadorId, x.VehiculoId, x.EmpresaId, Vence = x.FechaVencimiento!.Value })
                    .ToListAsync();
                return documentos.Select(x => (
                    x.TrabajadorId is not null ? "Trabajador"
                    : x.VehiculoId is not null ? "Vehículo"
                    : x.EmpresaId == propia ? "Empresa propia"
                    : x.EmpresaId is { } e && subcontratas.Contains(e) ? "Subcontrata" : "Otro",
                    x.Vence)).ToList();
            });
            salida.WriteLine($"MEDIDO {tenant.Clave}: {string.Join(", ", medidos[tenant.Clave].Select(m => $"{m.Ambito} {m.Vence:yyyy-MM-dd}"))}");
        }

        medidos["I"].Should().BeEmpty("MEDIDO: el escaparate al día no tiene nada vencido ni que venza en treinta días");

        medidos["J"].Should().OnlyContain(m => m.Vence == d.AddDays(4), "MEDIDO: todo lo que vence en treinta días vence exactamente a D+4");
        medidos["J"].GroupBy(m => m.Ambito).ToDictionary(g => g.Key, g => g.Count()).Should().BeEquivalentTo(
            new Dictionary<string, int> { ["Trabajador"] = 6, ["Empresa propia"] = 1, ["Subcontrata"] = 1, ["Vehículo"] = 1 });

        medidos["K"].Should().OnlyContain(m => m.Vence < d, "MEDIDO: lo único fuera de «en regla» está vencido; nada por vencer");
        medidos["K"].GroupBy(m => m.Ambito).ToDictionary(g => g.Key, g => g.Count()).Should().BeEquivalentTo(
            new Dictionary<string, int> { ["Trabajador"] = 3, ["Subcontrata"] = 1, ["Vehículo"] = 1 });

        foreach (var m in fixture.Informe.Tenants.Where(m => CatalogoPilotoOutbound.Escaparate.Any(t => t.Clave == m.Clave)))
            salida.WriteLine($"MEDIDO {m.Clave}: {PilotoOutboundTextos.CentrosPorEstado(m)}");
    }

    /// <summary>
    /// La rotación del Cuadro de dirección cuenta altas por <c>CreadoEnUtc</c> y bajas por <c>EliminadoEnUtc</c>: la siembra
    /// reparte las altas en el último año y da de baja, en meses anteriores, a los Trabajadores de baja del escaparate.
    /// </summary>
    [Fact]
    public async Task Las_altas_se_reparten_en_el_ultimo_ano_y_el_escaparate_tiene_bajas_en_meses_anteriores()
    {
        var ahora = DateTime.UtcNow;
        var inicioDelMes = new DateTime(ahora.Year, ahora.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var tenant in CatalogoPilotoOutbound.Escaparate)
        {
            var (altas, bajas) = await fixture.Arnes.EnTenantAsync(await fixture.Arnes.TenantIdAsync(tenant.Nombre), async (db, _) =>
            (
                await db.Trabajadores.AsNoTracking().Select(t => t.CreadoEnUtc).ToListAsync(),
                await db.Trabajadores.IgnoreQueryFilters().AsNoTracking().Where(t => t.EstaEliminado).Select(t => t.EliminadoEnUtc!.Value).ToListAsync()
            ));

            altas.Should().HaveCount(tenant.TrabajadoresSembrados, $"control: {tenant.Clave} tiene sus Trabajadores vivos");
            altas.Should().OnlyContain(a => a > ahora.AddDays(-366) && a <= ahora, $"MEDIDO: las altas de {tenant.Clave} caen en el último año");
            altas.Count(a => a >= inicioDelMes).Should().BeLessThan(altas.Count / 2, $"MEDIDO: en {tenant.Clave} no todas las altas son de este mes");
            bajas.Should().HaveCount(tenant.BajasDelUltimoAno, $"MEDIDO: {tenant.Clave} tiene sus bajas del último año");
            bajas.Should().OnlyContain(b => b < inicioDelMes && b > ahora.AddDays(-366), $"MEDIDO: las bajas de {tenant.Clave} son de meses anteriores");
        }
    }

    /// <summary>
    /// El retroceso de las fechas de vida de los Centros y los Clientes empresariales, y la vigencia de la Asignación de
    /// Operación que hace que «Centros nuevos» del Cuadro de dirección los cuente: en cada Tenant del lote, todo Centro entra
    /// dentro de la operación, después de su Cliente empresarial, y alguno entra en el mes de la siembra. Las bajas del
    /// escaparate quedan eliminadas por una cuenta, como lo haría la pantalla.
    /// </summary>
    [Fact]
    public async Task Los_Centros_y_los_Clientes_empresariales_entran_dentro_de_la_Asignacion_de_Operacion()
    {
        var ahora = DateTime.UtcNow;
        var inicioDelMes = new DateTime(ahora.Year, ahora.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            var (centros, clientes, operaciones, eliminadoresDeBajas) = await fixture.Arnes.EnTenantAsync(
                await fixture.Arnes.TenantIdAsync(tenant.Nombre), async (db, _) =>
                {
                    var centros = await db.Centros.AsNoTracking().Select(c => new { c.ClienteId, c.CreadoEnUtc }).ToListAsync();
                    var idsDeClientes = centros.Select(c => c.ClienteId).Distinct().ToList();
                    var clientes = await db.Empresas.AsNoTracking().Where(e => idsDeClientes.Contains(e.Id))
                        .ToDictionaryAsync(e => e.Id, e => e.CreadoEnUtc);
                    var operaciones = await db.AsignacionesOperacion.AsNoTracking()
                        .Where(o => !o.EsRaiz && o.OperadorTenantId != o.PropietarioTenantId).Select(o => o.VigenciaDesde).ToListAsync();
                    var eliminadores = await db.Trabajadores.IgnoreQueryFilters().AsNoTracking()
                        .Where(t => t.EstaEliminado).Select(t => t.EliminadoPorUsuarioId).ToListAsync();
                    return (centros.Select(c => (c.ClienteId, c.CreadoEnUtc)).ToList(), clientes, operaciones, eliminadores);
                });

            centros.Should().HaveCount(tenant.ClientesEmpresariales.Sum(c => c.Centros.Count), $"control: {tenant.Clave} tiene sus Centros");
            clientes.Should().HaveCount(tenant.ClientesEmpresariales.Count, $"control: {tenant.Clave} tiene sus Clientes empresariales");
            var inicio = operaciones.Should().ContainSingle($"MEDIDO: {tenant.Clave} tiene una operación externa").Subject;

            inicio.Should().Be(PilotoOutboundSeeder.InicioDeLaOperacionExterna(ArnesPilotoOutbound.FechaDemostracion()),
                $"MEDIDO: la operación de {tenant.Clave} empieza doce meses antes de la demostración");
            centros.Should().OnlyContain(c => c.CreadoEnUtc > inicio && c.CreadoEnUtc <= ahora,
                $"MEDIDO: los Centros de {tenant.Clave} entran dentro de la operación");
            centros.Should().OnlyContain(c => c.CreadoEnUtc > clientes[c.ClienteId],
                $"MEDIDO: cada Centro de {tenant.Clave} entra después de su Cliente empresarial");
            centros.Should().Contain(c => c.CreadoEnUtc >= inicioDelMes, $"MEDIDO: {tenant.Clave} tiene Centros nuevos en el mes de la siembra");
            clientes.Values.Should().OnlyContain(a => a > ahora.AddDays(-366) && a < ahora.AddDays(-199),
                $"MEDIDO: los Clientes empresariales de {tenant.Clave} entran en el último año, antes que sus Centros");
            eliminadoresDeBajas.Should().HaveCount(tenant.BajasDelUltimoAno,
                $"MEDIDO: {tenant.Clave} conserva todas las bajas previstas, también cuando no tiene ninguna");
            eliminadoresDeBajas.Should().NotContain(id => id == null,
                $"MEDIDO: las bajas de {tenant.Clave} las elimina una cuenta");

            salida.WriteLine($"MEDIDO {tenant.Clave}: {centros.Count(c => c.CreadoEnUtc >= inicioDelMes)} Centros nuevos este mes de {centros.Count}");
        }
    }

    [Fact]
    public async Task Ningun_nombre_completo_de_Trabajador_se_repite_en_los_Tenants_del_piloto()
    {
        var escritos = new List<(string Tenant, string NombreCompleto)>();
        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            var delTenant = await fixture.Arnes.EnTenantAsync(await fixture.Arnes.TenantIdAsync(tenant.Nombre), async (db, _) =>
                await db.Trabajadores.Select(t => t.Nombre + " " + t.Apellidos).ToListAsync());

            // Los que la base enseña son de los que el catálogo reparte: los de las subcontratas de T1 también.
            var delCatalogo = Enumerable.Range(0, CatalogoPilotoOutbound.TrabajadoresSembrados(tenant))
                .Select(i => IdentidadesPilotoOutbound.Trabajador(tenant, i)).Select(p => $"{p.Nombre} {p.Apellidos}").ToList();
            if (tenant.Perfil == PerfilPilotoOutbound.Autonomo) delCatalogo.Add(tenant.Nombre);
            delTenant.Should().NotBeEmpty().And.BeSubsetOf(delCatalogo, $"MEDIDO: los Trabajadores de {tenant.Clave} llevan los nombres del catálogo");

            escritos.AddRange(delTenant.Select(n => (tenant.Clave, n)));
        }

        escritos.GroupBy(e => e.NombreCompleto).Where(g => g.Count() > 1).Select(g => $"{g.Key}: {string.Join(", ", g.Select(e => e.Tenant))}")
            .Should().BeEmpty("MEDIDO: ningún nombre completo se repite entre los Trabajadores de los Tenants del piloto");
    }

    [Fact]
    public async Task La_siembra_no_envia_ni_deja_encolado_ningun_correo()
    {
        fixture.Arnes.Correo.Intentos.Should().BeEmpty("MEDIDO: ni la siembra ni la medición llaman a IEmailService");

        using (var ambito = fixture.Arnes.Servicios.CreateScope())
            ambito.ServiceProvider.GetRequiredService<IEmailService>().Should().BeSameAs(
                fixture.Arnes.Correo, "control positivo: el servicio de correo que resuelve la aplicación es el espía");

        var rastros = await fixture.Arnes.ComoBootstrapAsync(async b =>
        {
            var ids = await b.Tenants.Where(t => CatalogoPilotoOutbound.NombresTenants.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
            return new Dictionary<string, int>
            {
                ["Documentos (control positivo)"] = await b.Documentos.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Reclamaciones documentales"] = await b.ReclamacionesDocumentales.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
                ["Mensajes con estado de entrega"] = await b.Mensajes.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId) && e.EstadoEntrega != null),
                ["Notificaciones a usuarios"] = await b.NotificacionesUsuario.IgnoreQueryFilters().CountAsync(e => ids.Contains(e.TenantId)),
            };
        });

        salida.WriteLine("MEDIDO rastros de envío: " + string.Join(", ", rastros.Select(p => $"{p.Key} {p.Value}")));
        rastros["Documentos (control positivo)"].Should().BeGreaterThan(0, "control positivo: el recuento ve las filas de los Tenants del piloto");
        // Las reclamaciones históricas se siembran sin enviar: ningún mensaje tiene estado de entrega que un proceso pudiera recoger.
        rastros["Mensajes con estado de entrega"].Should().Be(0);
        rastros["Notificaciones a usuarios"].Should().Be(0);
    }

    /// <summary>
    /// La estructura que la autoverificación mide, escrita a mano aquí y no leída del catálogo: Trabajadores propios, de
    /// Subcontrata, Centros, Vehículos, Proyectos y Subcontratas de cada uno de los Tenants del lote, y la invariante de que
    /// cada Tenant tiene exactamente una Empresa propia y todo Centro cuelga de un Cliente empresarial del mismo Tenant.
    /// </summary>
    [Fact]
    public void Cada_perfil_tiene_la_estructura_y_las_invariantes_de_su_diseno()
    {
        // (Clave, Trabajadores propios, de Subcontrata, Centros, Vehículos, Proyectos, Subcontratas)
        (string, int, int, int, int, int, int)[] esperado =
        [
            ("A", 16, 0, 2, 2, 0, 0),
            ("B", 7, 0, 1, 1, 0, 0),
            ("C", 28, 0, 6, 4, 0, 0),
            ("D", 20, 0, 3, 6, 5, 0),
            ("E", 1, 0, 2, 1, 0, 0),
            ("F", 14, 14, 3, 3, 0, 3),
            ("G", 8, 0, 1, 1, 0, 0),
            ("H", 23, 4, 1, 1, 3, 1),
        ];

        foreach (var (clave, propios, deSubcontrata, centros, vehiculos, proyectos, subcontratas) in esperado)
        {
            var m = fixture.Informe.Tenants.Single(t => t.Clave == clave);
            salida.WriteLine($"MEDIDO {clave}: {m.Estructura}");
            (m.Estructura.TrabajadoresPropios, m.Estructura.TrabajadoresDeSubcontrata, m.Centros.Count, m.Estructura.Vehiculos,
                m.Estructura.Proyectos, m.Estructura.Subcontratas)
                .Should().Be((propios, deSubcontrata, centros, vehiculos, proyectos, subcontratas), $"MEDIDO: la estructura del perfil {clave}");
            m.Estructura.EmpresasPropias.Should().Be(1, $"{clave}: exactamente una Empresa propia");
            m.Estructura.CentrosFueraDeUnClienteDelTenant.Should().Be(0, $"{clave}: todo Centro cuelga de un Cliente empresarial del mismo Tenant");
            m.Estructura.EmpresasNoVisiblesParaLaGestora.Should().Be(0, $"{clave}: la Gestora CAE ve todas las Empresas del Tenant");
            m.Estructura.TrabajadoresSinFicha.Should().Be(0, $"{clave}: ningún Trabajador sin ficha");
            m.Estructura.SubcontratasSinTrabajadores.Should().Be(0, $"{clave}: ninguna Subcontrata decorativa");
            m.Estructura.VehiculosSinFicha.Should().Be(0);
            m.Estructura.AcreditacionesRechazadasSinMotivo.Should().Be(0);
        }

        var f = fixture.Informe.Tenants.Single(t => t.Clave == "F").Estructura;
        f.SubcontratasSupervisadas.Should().Be(1, "F tiene una Subcontrata Supervisada");
        f.VerificacionesExternas.Should().BeGreaterThan(0, "y su verificación externa");
        fixture.Informe.Tenants.Single(t => t.Clave == "D").Estructura.ProyectosAbiertos.Should().BeGreaterThan(0);
        fixture.Informe.Tenants.Where(t => t.Clave is "A" or "C" or "F").Should().OnlyContain(t => t.Estructura.DocumentosRenovados > 0);
        fixture.Informe.Tenants.Single(t => t.Clave == "C").Estructura
            .Should().Match<PilotoOutboundAutoverificacion.MedicionEstructura>(e => e.ExtranjerosConPermiso >= 2 && e.PermisosProximosACaducar >= 1,
                "C lleva extranjeros con permiso, uno próximo a caducar");
        fixture.Informe.Tenants.Single(t => t.Clave == "G").Estructura.PermisosCaducados.Should().Be(1);
        var h = fixture.Informe.Tenants.Single(t => t.Clave == "H").Estructura;
        (h.EventosTerminados, h.EventosEnCurso, h.EventosFuturos).Should().Be((1, 1, 1), "H tiene un evento en cada estado");
        (h.Voluntarios, h.VoluntariosInactivos).Should().Be((20, 7), "MEDIDO: los siete voluntarios que solo estuvieron en el evento terminado quedan inactivos");
        fixture.Informe.Tenants.Where(t => t.Clave != "H").Should().OnlyContain(t => t.Estructura.Voluntarios == 0, "solo H tiene voluntarios");
        fixture.Informe.Tenants.Where(t => t.Clave is "A" or "C" or "F" or "G").Should().OnlyContain(
            t => t.Estructura.VisitasFuturas + t.Estructura.VisitasRealizadas + t.Estructura.VisitasVencidas > 0, "las Visitas existen en el perfil");
    }

    [Fact]
    public async Task Un_re_arranque_con_la_fecha_de_demostracion_ya_pasada_avisa_y_no_escribe_porque_no_queda_nada_por_sembrar()
    {
        var antes = await fixture.Arnes.RecuentoAsync();
        var avisos = new ArnesPilotoOutbound.RegistroDeAvisos();
        var ayer = ArnesPilotoOutbound.Configurar(DiaDeNegocio.Hoy().AddDays(-1));

        var resultado = await fixture.Arnes.SembrarAsync(ayer, logger: avisos);

        resultado!.Escribio.Should().BeFalse("MEDIDO: no lanza; el arranque sigue");
        resultado.TenantsConDatosNuevos.Should().BeEmpty();
        avisos.Avisos.Should().ContainSingle().Which.Should().Contain("es anterior a hoy").And.Contain("no se escribe nada");
        (await fixture.Arnes.RecuentoAsync()).Should().BeEquivalentTo(antes, "MEDIDO: ni una fila, ni una cuenta, ni un fichero más");
    }

    [Fact]
    public async Task Los_resultados_no_cambian_en_ninguno_de_los_dias_en_que_se_puede_sembrar_para_esa_demostracion()
    {
        var fecha = ArnesPilotoOutbound.FechaDemostracion();
        DiaDeNegocio.Hoy().Should().Be(fecha.AddDays(-5), "control: sin fijar el reloj, hoy es D−5");

        // Los dos extremos: el primer día en que la fecha vale (D−9) y el propio día de la demostración.
        foreach (var dia in new[] { fecha.AddDays(-OpcionesPilotoOutbound.MargenMaximoDias), fecha })
        {
            Informe eseDia;
            using (DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojEn(dia.ToDateTime(new TimeOnly(11, 0), DateTimeKind.Utc))))
            {
                DiaDeNegocio.Hoy().Should().Be(dia, "control: el reloj fijado mueve el día de negocio");
                eseDia = await fixture.Arnes.MedirAsync(fixture.Configuracion);
            }

            PilotoOutboundAutoverificacion.Discrepancias(eseDia).Should().BeEmpty($"MEDIDO el día {dia:yyyy-MM-dd}");
        }
    }

    /// <summary>
    /// El bloqueo de acceso por Centro, medido en G con la evaluación de acceso que usan Inicio y el estado de los Centros:
    /// la aptitud médica que el Centro marca «bloquea el acceso» deja sin cumplir a quien la tiene vencida o ausente, y
    /// nada más bloquea. Y las dos reclamaciones sin respuesta de G, por el listado de la pantalla.
    /// </summary>
    [Fact]
    public async Task G_el_requisito_que_bloquea_el_acceso_y_las_reclamaciones_sin_respuesta_salen_en_sus_pantallas()
    {
        var g = CatalogoPilotoOutbound.G;
        var d = ArnesPilotoOutbound.FechaDemostracion();

        var (sinCumplir, sinRespuesta) = await fixture.Arnes.ComoGestoraPrimeraEnAsync(g.Nombre, async sp =>
        {
            var db = sp.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>();
            var nombreDelCentro = await db.Centros.ToDictionaryAsync(c => c.Id, c => c.Nombre);
            var nombreDelTipo = await db.TiposDocumento.ToDictionaryAsync(t => t.Id, t => t.Nombre);
            var evaluacion = await sp.GetRequiredService<CaeManager.Application.Centros.IEvaluacionDeAccesoPorCentroService>()
                .EvaluarAsync(null, CancellationToken.None);
            var sinCumplir = evaluacion.Requisitos
                .Where(r => r.Resultado.Situacion != SituacionDeRequisitoBloqueante.Cumplido
                    && !ReglaBloqueoDeAcceso.EsPendienteEnPlataforma(r.Resultado.Situacion))
                .Select(r => (r.TrabajadorId, Centro: nombreDelCentro[r.CentroId], Tipo: nombreDelTipo[r.TipoDocumentoId], r.Resultado.Situacion))
                .ToList();
            return (sinCumplir, await sp.GetRequiredService<ISender>().Send(new ObtenerReclamacionesSinRespuestaQuery()));
        });

        foreach (var r in sinCumplir)
            salida.WriteLine($"MEDIDO G requisito que bloquea sin cumplir: {r.Tipo} en «{r.Centro}», {r.Situacion}");

        sinCumplir.Should().NotBeEmpty("MEDIDO: G tiene vencida o ausente la aptitud que su Centro exige para acceder");
        sinCumplir.Should().OnlyContain(
            r => r.Tipo == CatalogoPilotoOutbound.AptitudMedica && r.Centro == g.Centros[0].Nombre,
            "MEDIDO: solo bloquea el tipo que el Centro marca, y en ese Centro");

        // La segunda reclamación es reciente (menos de una semana el primer día de siembra): la pantalla solo lista las de más de siete días.
        sinRespuesta.Should().HaveCount(1, "MEDIDO: de las dos reclamaciones de G, una lleva más de una semana sin respuesta y la otra es reciente");
        sinRespuesta.Max(r => r.DiasTranscurridos).Should().BeGreaterThanOrEqualTo(7, "una lleva más de una semana sin respuesta");
        sinRespuesta.Should().OnlyContain(r => DateOnly.FromDateTime(r.FechaEnvioUtc) < d);
    }

    /// <summary>
    /// La reclamación por acceso reclama lo que el Centro 360 pinta vencido o ausente aunque el Centro no marque nada «bloquea
    /// el acceso» (fallo del 2026-10-21: en C, la Fábrica de conservas de Calahorra salía «al día» con dos vencidos).
    /// </summary>
    [Fact]
    public async Task C_la_reclamacion_por_acceso_reclama_lo_que_el_Centro_360_pinta_vencido_o_ausente()
    {
        var centro = C.Centros[4].Nombre;

        var (documentacion, lotes) = await fixture.Arnes.ComoGestoraPrimeraEnAsync(C.Nombre, async sp =>
        {
            var db = sp.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>();
            var centroId = await db.Centros.Where(c => c.Nombre == centro).Select(c => c.Id).SingleAsync();
            var sender = sp.GetRequiredService<ISender>();
            var documentacion = await sender.Send(
                new CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro.ObtenerAsignacionesDocumentacionPorCentroQuery(centroId));
            // Los Trabajadores de la Empresa que más tiene en el Centro (una cedida por ETT sería de otra Empresa empleadora).
            var porEmpresa = await db.Trabajadores.Where(t => documentacion.Select(d => d.TrabajadorId).Contains(t.Id) && t.EmpresaId != null)
                .Select(t => new { t.Id, EmpresaId = t.EmpresaId!.Value }).ToListAsync();
            var empresa = porEmpresa.GroupBy(t => t.EmpresaId).OrderByDescending(g => g.Count()).First();
            var trabajadorIds = empresa.Select(t => t.Id).ToList();
            var lotes = await sender.Send(
                new CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionAEmpresaEmpleadora.ObtenerReclamacionPorAccesoQuery(empresa.Key, centroId, trabajadorIds));
            return (documentacion.Where(d => trabajadorIds.Contains(d.TrabajadorId)).ToList(), lotes);
        });

        var vencidos = documentacion.SelectMany(t => t.Documentos.Where(x => x.Estado == EstadoDocumento.Vencido).Select(x => x.DocumentoId!.Value)).ToList();
        var ausentes = documentacion.SelectMany(t => t.Documentos.Where(x => x.Estado == EstadoDocumento.Faltante).Select(x => (t.TrabajadorId, x.TipoDocumentoId))).ToList();
        salida.WriteLine($"MEDIDO C «{centro}»: {vencidos.Count} vencidos y {ausentes.Count} ausentes en Centro 360; {lotes.Count} lotes por acceso");

        vencidos.Should().NotBeEmpty("control: la siembra deja vencidos en este Centro");
        ausentes.Should().NotBeEmpty("control: la siembra deja documentos de Trabajador sin subir en este Centro");
        lotes.SelectMany(l => l.Documentos.Select(x => x.DocumentoId)).Should().Contain(vencidos);
        lotes.SelectMany(l => (l.PendientesSinFecha ?? []).Where(p => p.TrabajadorId != null).Select(p => (p.TrabajadorId!.Value, p.TipoDocumentoId)))
            .Should().Contain(ausentes);
    }

    /// <summary>La tolerancia de un Centro: el mismo documento vencido se pinta «en tolerancia» donde el Centro la concede.</summary>
    [Fact]
    public async Task C_el_documento_vencido_en_el_Centro_con_tolerancia_se_pinta_en_tolerancia()
    {
        var tolerancia = C.Tolerancia!;
        var centro = C.Centros[tolerancia.Centro].Nombre;
        var d = ArnesPilotoOutbound.FechaDemostracion();

        var formacionPorCentro = await fixture.Arnes.ComoGestoraPrimeraEnAsync(C.Nombre, async sp =>
        {
            var db = sp.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>();
            var vencimientoDelCaso = (DateOnly?)d.AddDays(-tolerancia.DiasDesdeQueVencio);
            var formacionId = await db.TiposDocumento.Where(t => t.Nombre == tolerancia.Tipo).Select(t => t.Id).SingleAsync();
            var conLaFormacionVencida = await (
                from a in db.Asignaciones
                join c in db.Centros on a.CentroId equals c.Id
                join x in db.Documentos on (Guid?)a.TrabajadorId equals x.TrabajadorId
                where a.FechaBaja == null && c.Nombre == centro && x.TipoDocumentoId == formacionId && x.FechaVencimiento == vencimientoDelCaso
                select a.TrabajadorId).Distinct().ToListAsync();
            var ficha = await sp.GetRequiredService<ISender>().Send(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(conLaFormacionVencida[0]));
            return ficha.Where(c => c.CentroNombre == centro).Select(c => c.Documentos.Single(x => x.TipoDocumentoId == formacionId)).ToList();
        });

        var enTolerancia = formacionPorCentro.Should().ContainSingle().Subject;
        enTolerancia.Estado.Should().Be(EstadoDocumento.EnTolerancia, "MEDIDO: venció hace unos días y su Centro concede tolerancia");
        enTolerancia.FechaVencimiento.Should().Be(d.AddDays(-tolerancia.DiasDesdeQueVencio));
        enTolerancia.EnToleranciaHasta.Should().Be(d.AddDays(tolerancia.Dias - tolerancia.DiasDesdeQueVencio));
    }

    /// <summary>
    /// R13: el Centro de C que se gestiona por correo, su Visita y lo que viaja en el ZIP, por las tres consultas de la
    /// pantalla de la Visita: lo que la comprobación previa da por entregable es exactamente lo que lleva el ZIP.
    /// </summary>
    [Fact]
    public async Task C_la_Visita_al_Centro_que_se_gestiona_por_correo_lleva_en_su_ZIP_lo_que_ese_Centro_exige()
    {
        var d = ArnesPilotoOutbound.FechaDemostracion();
        var centroPorCorreo = C.Centros[C.CentroPorCorreo!.Value].Nombre;

        var (visita, previa, solicitud, descarga) = await fixture.Arnes.ComoGestoraPrimeraEnAsync(C.Nombre, async sp =>
        {
            var db = sp.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>();
            var sender = sp.GetRequiredService<ISender>();
            var visita = await (from v in db.Visitas
                                join c in db.Centros on v.CentroId equals c.Id
                                where v.Origen == OrigenVisita.Correo
                                select new { v.Id, v.FechaInicio, Centro = c.Nombre }).SingleAsync();

            return (
                visita,
                await sender.Send(new ObtenerDocumentacionVisitaQuery(visita.Id)),
                await sender.Send(new ObtenerSolicitudAccesoCorreoQuery(visita.Id)),
                await sender.Send(new ObtenerPaqueteDocumentalVisitaQuery(visita.Id)));
        });

        (visita.Centro, visita.FechaInicio).Should().Be((centroPorCorreo, d.AddDays(4)), "MEDIDO: la única Visita por correo de C");

        solicitud.EsExitoso.Should().BeTrue();
        salida.WriteLine($"MEDIDO C solicitud de acceso por correo: a {solicitud.Valor.Destinatarios} · {solicitud.Valor.Asunto}");
        solicitud.Valor.Destinatarios.Should().StartWith("ensayo+accesos.").And.EndWith("@destino.example",
            "MEDIDO: el canal principal del Centro es un correo de la misma regla que la agenda");

        previa.Should().NotBeNull();
        previa!.Trabajadores.Should().HaveCount(2).And.OnlyContain(
            t => t.Documentacion.Documentos.Count > 0 && t.Documentacion.Documentos.All(x => x.DocumentoId != null && x.Estado == EstadoDocumento.Vigente),
            "MEDIDO: dos Trabajadores con todo en regla; los documentos que no caducan no se listan");
        previa.Empresa.Documentos.Should().NotContain(x => x.Estado == EstadoDocumento.Faltante);

        descarga.EsExitoso.Should().BeTrue();
        using var zip = new ZipArchive(new MemoryStream(descarga.Valor.Contenido), ZipArchiveMode.Read);
        var entradas = zip.Entries.Select(e => e.FullName).ToList();
        salida.WriteLine($"MEDIDO C ZIP «{descarga.Valor.NombreArchivo}», {descarga.Valor.Contenido.Length} bytes: {string.Join(" | ", entradas)}");

        // La pantalla no lista lo que no caduca (la información de riesgos), pero el ZIP lleva los cinco tipos exigidos de cada Trabajador.
        var deTrabajador = previa.Trabajadores.Count * CatalogoPilotoOutbound.TiposExigidosDeTrabajador.Count;
        var deEmpresa = previa.Empresa.Documentos.Count(x => x.DocumentoId != null && x.Estado != EstadoDocumento.Vencido);
        entradas.Count(e => e.StartsWith("Trabajadores", StringComparison.Ordinal)).Should().Be(deTrabajador, "MEDIDO: viaja cada documento de Trabajador que la pantalla da por entregable");
        entradas.Count(e => e.StartsWith("Empresa", StringComparison.Ordinal)).Should().Be(deEmpresa, "MEDIDO: y cada documento de Empresa que no está vencido");
        entradas.Should().HaveCount(deTrabajador + deEmpresa, "MEDIDO: y nada más");
    }

    /// <summary>La cuenta opcional de § 3.3: un Usuario de Cliente de C, ligado a su primer Cliente empresarial.</summary>
    [Fact]
    public async Task C_tiene_un_Usuario_de_Cliente_empresarial_ligado_a_su_primer_Cliente_empresarial()
    {
        var tenantId = await fixture.Arnes.TenantIdAsync(C.Nombre);
        var email = CuentasPilotoOutbound.Locales.UsuarioDeClienteEmpresarialT1;

        var (tenantDeLaCuenta, roles, clienteEmpresarial) = await fixture.Arnes.EnTenantAsync(tenantId, async (db, sp) =>
        {
            var usuarios = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var cuenta = await usuarios.FindByEmailAsync(email) ?? throw new InvalidOperationException("La cuenta no existe.");
            var suCliente = await db.Empresas.Where(e => e.Id == cuenta.ClienteId).Select(e => e.RazonSocial).SingleOrDefaultAsync();
            return (cuenta.TenantId, await usuarios.GetRolesAsync(cuenta), suCliente);
        });

        tenantDeLaCuenta.Should().Be(tenantId, "MEDIDO: la cuenta es del Tenant propietario, no del Operador CAE externo");
        roles.Should().Equal("Cliente");
        clienteEmpresarial.Should().Be(C.ClientesEmpresariales[0].RazonSocial);
    }

    /// <summary>
    /// Cada Gestor CAE ve en Mi trabajo solo su cartera: la Gestora CAE primera, B, D, E y G (exclusivos) y C (compartido);
    /// el segundo, A, F y H (exclusivos) y C.
    /// </summary>
    [Fact]
    public async Task Cada_Gestor_CAE_ve_en_Mi_trabajo_sus_Tenants_exclusivos_y_el_compartido()
    {
        var idPorClave = new Dictionary<string, Guid>();
        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
            idPorClave[tenant.Clave] = await fixture.Arnes.TenantIdAsync(tenant.Nombre);

        async Task<List<string>> ClavesVistasPorAsync(string email)
        {
            var miTrabajo = await fixture.Arnes.ComoCuentaAsync(
                CatalogoPilotoOutbound.NombreTenantOperador, email,
                sp => sp.GetRequiredService<ISender>().Send(new ObtenerMiTrabajoAgregadoQuery()));
            var vistos = miTrabajo.Tenants.Select(t => t.TenantId).Concat(miTrabajo.NoConsultados.Select(t => t.TenantId)).ToHashSet();
            miTrabajo.NoConsultados.Should().BeEmpty();
            return [.. idPorClave.Where(p => vistos.Contains(p.Value)).Select(p => p.Key).Order()];
        }

        (await ClavesVistasPorAsync(CuentasPilotoOutbound.Locales.GestorSegundo)).Should().Equal("A", "C", "F", "H", "J");
        (await ClavesVistasPorAsync(CuentasPilotoOutbound.Locales.GestoraPrimera)).Should().Equal("B", "C", "D", "E", "G", "I", "K");
    }

    /// <summary>
    /// Sensibilidad de las comprobaciones del equipo del Operador CAE externo, del reparto de carteras y del perfil H:
    /// lo medido en verde, apartado en un solo dato, da exactamente la discrepancia que nombra ese dato.
    /// </summary>
    [Fact]
    public void Las_comprobaciones_del_equipo_las_carteras_y_los_eventos_se_ponen_en_rojo_si_lo_medido_se_aparta()
    {
        var informe = fixture.Informe;
        PilotoOutboundAutoverificacion.Discrepancias(informe).Should().BeEmpty("control: sin apartar nada, en verde");

        var h = informe.De(CatalogoPilotoOutbound.H);
        PilotoOutboundAutoverificacion.Informe ConH(PilotoOutboundAutoverificacion.MedicionEstructura e) =>
            informe with { Tenants = [.. informe.Tenants.Select(t => t.Clave == "H" ? h with { Estructura = e } : t)] };
        void UnaSola(PilotoOutboundAutoverificacion.Informe apartado, string fragmento) =>
            PilotoOutboundAutoverificacion.Discrepancias(apartado).Should().ContainSingle()
                .Which.Should().Contain(fragmento);

        UnaSola(ConH(h.Estructura with { VoluntariosInactivos = 0 }), "H «" + CatalogoPilotoOutbound.NombreTenantH + "» · Trabajadores · voluntarios inactivos");
        UnaSola(ConH(h.Estructura with { EventosEnCurso = 0, Proyectos = h.Estructura.Proyectos }), "Eventos · en curso");
        UnaSola(ConH(h.Estructura with { EventosTerminados = 0 }), "Eventos · terminados");
        UnaSola(ConH(h.Estructura with { EventosFuturos = 2 }), "Eventos · futuros");
        UnaSola(ConH(h.Estructura with { EmpresasPropias = 2 }), "Empresas con EsPropia");
        UnaSola(
            ConH(h.Estructura with { RazonesSociales = [.. h.Estructura.RazonesSociales, "Empresa ajena al catálogo, S.L."] }),
            "razón social que no es del catálogo inventado del piloto: medido Empresa ajena al catálogo, S.L.");

        UnaSola(informe with { Operador = informe.Operador! with { DireccionCaeConSuRol = false } }, "cuenta de Dirección CAE");

        var ia = informe.AsistenteIa!;
        UnaSola(
            informe with { AsistenteIa = ia with { Gestora = ia.Gestora with { ConInstruccion = [.. ia.Gestora.ConInstruccion, CatalogoPilotoOutbound.NombreTenantH] } } },
            $"cartera de la Gestora CAE · «{CatalogoPilotoOutbound.NombreTenantH}»: medido en la cartera, esperado fuera");
        UnaSola(
            informe with { AsistenteIa = ia with { GestorSegundo = ia.GestorSegundo with { ConInstruccion = [.. ia.GestorSegundo.ConInstruccion.Where(n => n != CatalogoPilotoOutbound.NombreTenantC)] } } },
            $"cartera del segundo Gestor CAE · «{CatalogoPilotoOutbound.NombreTenantC}»: medido fuera de la cartera");

        // El reparto del catálogo: sin exclusivos del segundo Gestor CAE ni compartido, dos discrepancias; tal cual, ninguna.
        CatalogoPilotoOutbound.DiscrepanciasDelRepartoDeCarteras(CatalogoPilotoOutbound.Tenants).Should().BeEmpty();
        CatalogoPilotoOutbound.DiscrepanciasDelRepartoDeCarteras(
                [.. CatalogoPilotoOutbound.Tenants.Select(t => t with { Cartera = CarteraPilotoOutbound.GestoraPrimera })])
            .Should().HaveCount(2).And.Contain(l => l.Contains("segundo Gestor CAE no tiene ningún Tenant exclusivo"))
            .And.Contain(l => l.Contains("exactamente un Tenant compartido"));
    }

    /// <summary>Las tres señales de la IA y de los correos de C, y la cuenta pendiente del Operador CAE externo, tal como las cuenta la base.</summary>
    [Fact]
    public async Task C_lleva_una_revision_de_IA_una_deteccion_de_alta_y_una_sugerencia_de_Visita_pendientes()
    {
        var recuento = await fixture.Arnes.RecuentoAsync();
        var conSenales = CatalogoPilotoOutbound.Tenants.Count(t => t.ConSenalesDeIa);
        CatalogoPilotoOutbound.Tenants.Where(t => t.ConSenalesDeIa).Select(t => t.Clave).Should().Equal(["C", "I", "J", "K"], "control: C y el escaparate");
        new[] { "Revisiones de IA", "Detecciones de Trabajador", "Sugerencias de Visita" }.Select(k => recuento[k])
            .Should().Equal([conSenales, conSenales, conSenales], "MEDIDO: una de cada en C y en cada Tenant del escaparate");
    }

    /// <summary>Las Visitas de cada estado: futuras, en curso, realizadas, vencidas (pasaron sin gestionar) y canceladas.</summary>
    [Fact]
    public void Las_Visitas_se_siembran_en_sus_cinco_estados_y_una_es_urgente()
    {
        var visitas = fixture.Informe.Tenants.Select(t => t.Estructura).ToList();
        visitas.Sum(e => e.VisitasFuturas).Should().BeGreaterThan(0);
        visitas.Sum(e => e.VisitasRealizadas).Should().BeGreaterThan(0);
        visitas.Sum(e => e.VisitasVencidas).Should().BeGreaterThan(0);
        visitas.Sum(e => e.VisitasCanceladas).Should().BeGreaterThan(0);
        visitas.Sum(e => e.VisitasEnCurso).Should().BeGreaterThan(0, "MEDIDO: hay una Visita en curso");
    }

    /// <summary>
    /// «En curso» y «urgente» en perfiles distintos, con las cifras escritas a mano: la de D empezó diez días antes de la
    /// demostración y sigue en curso todos los días en que se puede sembrar (D−9 a D); la de B empieza un día después de la
    /// demostración, así que cae dentro de las 48 h de aviso solo en D−1 y D (el día de medición por defecto, D−5, aún no).
    /// </summary>
    [Fact]
    public async Task D_tiene_una_Visita_en_curso_cualquier_dia_de_la_siembra_y_B_una_a_menos_de_48_horas_el_dia_de_la_demostracion()
    {
        var fecha = ArnesPilotoOutbound.FechaDemostracion();
        var (d, b) = ("D", "B");

        foreach (var dia in new[] { fecha.AddDays(-OpcionesPilotoOutbound.MargenMaximoDias), fecha.AddDays(-5), fecha.AddDays(-2), fecha.AddDays(-1), fecha })
        {
            Informe eseDia;
            using (DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojEn(dia.ToDateTime(new TimeOnly(11, 0), DateTimeKind.Utc))))
                eseDia = await fixture.Arnes.MedirAsync(fixture.Configuracion);

            var enD = eseDia.Tenants.Single(t => t.Clave == d);
            var enB = eseDia.Tenants.Single(t => t.Clave == b);
            salida.WriteLine($"MEDIDO {dia:yyyy-MM-dd}: D en curso {enD.Estructura.VisitasEnCurso} urgentes en Inicio {enD.InicioVisitasUrgentes}; " +
                             $"B con aviso urgente {enB.Estructura.VisitasConAvisoUrgente} urgentes en Inicio {enB.InicioVisitasUrgentes}");

            (enD.Estructura.VisitasEnCurso, enD.InicioVisitasUrgentes).Should().Be((1, 1), $"MEDIDO el día {dia:yyyy-MM-dd}: la Visita en curso de D cuenta como urgente en Inicio");
            // Empieza en D+1: dentro de las 48 h de aviso el día D−1 (empieza a dos días) y el propio D, no antes.
            var urgenteDeB = dia >= fecha.AddDays(-1) ? 1 : 0;
            (enB.Estructura.VisitasConAvisoUrgente, enB.InicioVisitasUrgentes).Should().Be((urgenteDeB, urgenteDeB), $"MEDIDO el día {dia:yyyy-MM-dd}");
            // I, el escaparate grande, lleva su propia Visita en curso desde D−10, como la de D.
            var enI = eseDia.Tenants.Single(t => t.Clave == CatalogoPilotoOutbound.I.Clave);
            (enI.Estructura.VisitasEnCurso, enI.Estructura.VisitasConAvisoUrgente).Should().Be((1, 0), $"MEDIDO el día {dia:yyyy-MM-dd}: la Visita en curso de I");
            eseDia.Tenants.Where(t => t.Clave is not ("D" or "B" or "I")).Should().OnlyContain(
                t => t.Estructura.VisitasEnCurso == 0 && t.Estructura.VisitasConAvisoUrgente == 0, "ningún otro perfil tiene una Visita en curso ni a menos de 48 h");
        }
    }

    /// <summary>
    /// Prueba de flujos del 2026-10-21 (fila 19): en B, una Asignación nueva al Centro con plataforma hacía nacer de golpe
    /// una acreditación PendienteDeSubir por cada documento de la Empresa propia, y Mi trabajo pasaba de cero a trece
    /// propagaciones pendientes sin que nadie hubiera tocado un documento de Empresa. La regla de alta del producto es la
    /// correcta (P0-7: una Asignación pone en juego los documentos del Trabajador y de su Empresa ante ese Centro); lo que
    /// fallaba era la siembra, que no los había acreditado aunque la Empresa ya tenía Trabajadores allí. Se reproduce la
    /// acción con el servicio real, sin guardar: ningún documento de Empresa queda por acreditar.
    /// </summary>
    [Fact]
    public async Task Una_Asignacion_nueva_al_Centro_con_plataforma_no_acredita_ningun_documento_de_Empresa_que_la_siembra_no_hubiera_acreditado()
    {
        var conPlataforma = CatalogoPilotoOutbound.Tenants.Where(t => t.CentroConPlataforma is not null).ToList();
        conPlataforma.Select(t => t.Clave).Should().Contain("B", "control: B es el Tenant de la prueba de flujos");

        foreach (var tenant in conPlataforma)
        {
            var (deEmpresaYaAcreditados, deEmpresaNuevos) = await fixture.Arnes.EnTenantAsync(await fixture.Arnes.TenantIdAsync(tenant.Nombre), async (db, sp) =>
            {
                var canal = await db.CanalesGestionDocumental.AsNoTracking().SingleAsync(c => c.Tipo == TipoCanalGestion.Plataforma);
                var propia = await db.Empresas.AsNoTracking().Where(e => e.EsPropia).Select(e => e.Id).SingleAsync();
                var trabajadorId = await (
                    from asignacion in db.Asignaciones.AsNoTracking()
                    join trabajador in db.Trabajadores.AsNoTracking() on asignacion.TrabajadorId equals trabajador.Id
                    where asignacion.CentroId == canal.CentroId && asignacion.FechaBaja == null && trabajador.EmpresaId == propia
                    orderby trabajador.Id
                    select trabajador.Id).FirstAsync();
                var documentosDeEmpresa = await db.Documentos.AsNoTracking().Operativos()
                    .Where(d => d.EmpresaId == propia && d.TrabajadorId == null).Select(d => d.Id).ToListAsync();
                var yaAcreditados = await db.AcreditacionesDocumentoPlataforma.AsNoTracking()
                    .CountAsync(a => a.CanalGestionDocumentalId == canal.Id && documentosDeEmpresa.Contains(a.DocumentoId));

                await sp.GetRequiredService<IAltaAcreditacionesPlataformaService>().AgregarPendientesAsync(
                    new AltasConAcreditacion { Asignaciones = [new Asignacion(trabajadorId, canal.CentroId, DiaDeNegocio.Hoy())] });

                // Sin SaveChanges: lo agregado se queda en el seguimiento de este ámbito y se descarta con él.
                var nuevos = db.ChangeTracker.Entries<AcreditacionDocumentoPlataforma>()
                    .Where(e => e.State == EntityState.Added && documentosDeEmpresa.Contains(e.Entity.DocumentoId))
                    .Count();
                return (yaAcreditados, nuevos);
            });

            salida.WriteLine($"MEDIDO {tenant.Clave}: {deEmpresaYaAcreditados} documentos de Empresa acreditados en el Centro con plataforma; una Asignación nueva añade {deEmpresaNuevos}");
            deEmpresaYaAcreditados.Should().BeGreaterThan(0, $"control {tenant.Clave}: la Empresa propia tiene documentos que acreditar allí");
            deEmpresaNuevos.Should().Be(0, $"{tenant.Clave}: la siembra ya acreditó lo que la regla de alta exige para la Empresa en ese Centro");
        }
    }

    /// <summary>
    /// Los tres tipos de #1363 llegan a cada Tenant del piloto por la copia del catálogo y se usan en sus perfiles —el REA
    /// y un Libro de Subcontratación por obra en D, el recibo de RETA en E— en vez de los sustitutos de antes. Escritos a mano.
    /// </summary>
    [Fact]
    public async Task Los_tipos_de_REA_Libro_de_Subcontratacion_y_RETA_estan_en_cada_Tenant_y_los_usan_D_y_E_y_no_sus_sustitutos()
    {
        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            var (ambitos, enUso, sustituto) = await fixture.Arnes.EnTenantAsync(await fixture.Arnes.TenantIdAsync(tenant.Nombre), async (db, _) =>
            {
                var tipos = await db.TiposDocumento.AsNoTracking().ToListAsync();
                var usados = await db.Documentos.AsNoTracking().Select(x => x.TipoDocumentoId).ToListAsync();
                return (
                    tipos.Where(t => t.Nombre is "Certificado de inscripción en el REA" or "Libro de Subcontratación" or "Recibo de cuota de autónomos (RETA)")
                        .ToDictionary(t => t.Nombre, t => (t.AmbitoAplicacion, t.VigenciaMeses)),
                    tipos.Where(t => usados.Contains(t.Id)).GroupBy(t => t.Nombre).ToDictionary(g => g.Key, g => usados.Count(u => g.Any(t => t.Id == u))),
                    tipos.Any(t => t.Nombre is "Relación de Maquinaria") && usados.Any(u => tipos.Any(t => t.Id == u && t.Nombre == "Relación de Maquinaria")));
            });

            ambitos.Should().BeEquivalentTo(new Dictionary<string, (AmbitoAplicacion, int?)>
            {
                ["Certificado de inscripción en el REA"] = (AmbitoAplicacion.Empresa, 36),
                ["Libro de Subcontratación"] = (AmbitoAplicacion.Proyecto, null),
                ["Recibo de cuota de autónomos (RETA)"] = (AmbitoAplicacion.Empresa, 1),
            }, $"MEDIDO {tenant.Clave}: el Tenant recibió los tres tipos por la copia del catálogo");

            sustituto.Should().BeFalse($"{tenant.Clave}: ningún documento usa ya «Relación de Maquinaria»");

            // Que los Centros del perfil pidan el tipo: los tres de D el REA y los dos de E el recibo; los demás, ninguno de los tres.
            var pedidos = await fixture.Arnes.EnTenantAsync(await fixture.Arnes.TenantIdAsync(tenant.Nombre), (db, _) =>
                (from requisito in db.TiposDocumentoCentros.AsNoTracking()
                 join tipo in db.TiposDocumento.AsNoTracking() on requisito.TipoDocumentoId equals tipo.Id
                 where requisito.Incluido && (tipo.Nombre == "Certificado de inscripción en el REA" || tipo.Nombre == "Recibo de cuota de autónomos (RETA)")
                 group requisito by tipo.Nombre into g
                 select new { Nombre = g.Key, Centros = g.Count() }).ToDictionaryAsync(x => x.Nombre, x => x.Centros));
            pedidos.Should().BeEquivalentTo(tenant.Clave switch
            {
                "D" => new Dictionary<string, int> { ["Certificado de inscripción en el REA"] = 3 },
                "E" => new Dictionary<string, int> { ["Recibo de cuota de autónomos (RETA)"] = 2 },
                _ => []
            }, $"MEDIDO {tenant.Clave}: los Centros que piden el REA o el recibo de RETA");
            (enUso.GetValueOrDefault("Certificado de inscripción en el REA"), enUso.GetValueOrDefault("Libro de Subcontratación"),
                enUso.GetValueOrDefault("Recibo de cuota de autónomos (RETA)"))
                .Should().Be(tenant.Clave switch { "D" => (1, 5, 0), "E" => (0, 0, 1), _ => (0, tenant.Obras.Count, 0) },
                    $"MEDIDO {tenant.Clave}: dónde se usan (un Libro de Subcontratación por obra: D y el escaparate tienen obras)");
        }
    }

    // Los dos tipos de documento que vencen solos al mes, escritos a mano.
    private static readonly string[] CertificadosMensuales = ["Certificado de estar al corriente con la Seguridad Social", "ITA"];

    /// <summary>
    /// Lo que la comprobación previa lista de una Empresa propia en regla: cada fila con
    /// su documento, «Próximo» los dos certificados mensuales —un documento mensual
    /// coherente con su tipo no puede estar a más de 31 días de vencer— y Vigente lo demás.
    /// </summary>
    private static void ExigirDocumentosDeEmpresaEnRegla(IReadOnlyList<DocumentoVisitaItemDto> documentos)
    {
        documentos.Should().OnlyContain(x => x.DocumentoId != null);
        documentos.Where(x => x.Estado == EstadoDocumento.Proximo).Select(x => x.TipoDocumentoNombre).Should().BeEquivalentTo(CertificadosMensuales);
        documentos.Where(x => x.Estado != EstadoDocumento.Proximo).Should().NotBeEmpty().And.OnlyContain(x => x.Estado == EstadoDocumento.Vigente);
    }

    /// <summary>
    /// Las dos propiedades de fechas de la autoverificación y la regla de «no caduca»,
    /// leídas directamente de las filas de los seis Tenants: el vencimiento de un Tipo
    /// que vence solo es su emisión más los meses del Tipo, ninguna emisión es futura
    /// —ni lo habría sido el primer día en que se podía sembrar— y solo quedan «no
    /// caduca» los Tipos cuya nota del catálogo dice que no tienen caducidad.
    /// </summary>
    [Fact]
    public async Task Las_fechas_de_cada_documento_son_las_que_el_producto_le_habria_dado_y_solo_seis_tipos_quedan_sin_caducidad()
    {
        var d = ArnesPilotoOutbound.FechaDemostracion();
        var hoy = DiaDeNegocio.Hoy();
        var primerDiaDeSiembra = d.AddDays(-OpcionesPilotoOutbound.MargenMaximoDias);
        string[] deFechaManualNombrados = ["Certificado de estar al corriente con Hacienda", "Seguro de Responsabilidad Civil + recibo de pago"];
        var (conVencimientoAutomatico, sinCaducidadEnTotal) = (0, 0);

        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            var filas = await fixture.Arnes.EnTenantAsync(await fixture.Arnes.TenantIdAsync(tenant.Nombre), async (db, _) =>
            {
                var tipos = await db.TiposDocumento.AsNoTracking().ToDictionaryAsync(t => t.Id);
                var documentos = await db.Documentos.AsNoTracking()
                    .Select(x => new { x.TipoDocumentoId, x.FechaEmision, x.FechaVencimiento, x.EstadoVigencia }).ToListAsync();
                return documentos.Select(x => (Tipo: tipos[x.TipoDocumentoId], x.FechaEmision, x.FechaVencimiento, x.EstadoVigencia)).ToList();
            });

            var deTipoQueVenceSolo = filas.Where(f => f.Tipo.FijaVigenciaDesdeLaEmision).ToList();
            var ajenosASuTipo = deTipoQueVenceSolo
                .Where(f => f.FechaVencimiento != CalculadoraEstadoDocumento.CalcularFechaVencimiento(f.FechaEmision, f.Tipo.VigenciaMeses)).ToList();
            var emitidosDespuesDeHoy = filas.Where(f => f.FechaEmision > hoy).ToList();
            var emitidosDespuesDelPrimerDia = filas.Where(f => f.FechaEmision > primerDiaDeSiembra).ToList();
            var sinCaducidad = filas.Where(f => f.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca).ToList();
            var mensuales = deTipoQueVenceSolo.Where(f => f.Tipo.VigenciaMeses == 1).ToList();
            var trimestrales = deTipoQueVenceSolo.Where(f => f.Tipo.VigenciaMeses == 3).ToList();
            conVencimientoAutomatico += deTipoQueVenceSolo.Count;

            salida.WriteLine(
                $"MEDIDO {tenant.Clave}: {filas.Count} documentos, {deTipoQueVenceSolo.Count} de un Tipo que vence solo; con vencimiento ajeno a su Tipo " +
                $"{ajenosASuTipo.Count}; emitidos después de hoy {emitidosDespuesDeHoy.Count}; emitidos después de D−{OpcionesPilotoOutbound.MargenMaximoDias} " +
                $"{emitidosDespuesDelPrimerDia.Count}; emisión más tardía {(filas.Count == 0 ? "(sin documentos)" : $"{filas.Max(f => f.FechaEmision):yyyy-MM-dd}")}; " +
                $"sin caducidad {sinCaducidad.Count} [{string.Join("; ", sinCaducidad.GroupBy(f => f.Tipo.Nombre).Select(g => $"{g.Key} {g.Count()}"))}]; " +
                $"mensuales vencen en [{string.Join(", ", mensuales.Select(f => $"D+{f.FechaVencimiento!.Value.DayNumber - d.DayNumber}"))}]; " +
                $"trimestrales vencen en [{string.Join(", ", trimestrales.Select(f => $"D+{f.FechaVencimiento!.Value.DayNumber - d.DayNumber}"))}]");

            ajenosASuTipo.Select(f => $"{f.Tipo.Nombre}: {f.FechaEmision:yyyy-MM-dd} → {f.FechaVencimiento:yyyy-MM-dd}").Should().BeEmpty(
                $"{tenant.Clave}: el producto no deja crear un documento de un Tipo que vence solo con otro vencimiento que su emisión más los meses del Tipo");
            emitidosDespuesDeHoy.Should().BeEmpty($"{tenant.Clave}: ninguna emisión es futura");
            emitidosDespuesDelPrimerDia.Should().BeEmpty($"{tenant.Clave}: ninguna emisión habría sido futura el primer día en que se podía sembrar");
            sinCaducidad.Select(f => f.Tipo.Nombre).Distinct().Should().BeSubsetOf(
                CatalogoPilotoOutbound.TiposQueNoCaducan, $"{tenant.Clave}: solo quedan «no caduca» los Tipos cuya nota del catálogo lo dice");
            // Por negación y no con OnlyContain, que da rojo ante una colección vacía: el Tenant con todo
            // pendiente no tiene documentos. Que en los demás había de las tres clases lo afirma el control de abajo.
            filas.Where(f => deFechaManualNombrados.Contains(f.Tipo.Nombre) && f.EstadoVigencia != EstadoVigenciaDocumento.VenceEnFecha)
                .Select(f => f.Tipo.Nombre).Should().BeEmpty($"{tenant.Clave}: Hacienda y el seguro llevan su fecha, anotada a mano");
            mensuales.Select(f => f.FechaVencimiento!.Value.DayNumber - d.DayNumber).Where(dias => dias is < 16 or > 21).Should().BeEmpty(
                $"{tenant.Clave}: un mensual está «Próximo», y nunca «Urgente», de D−9 a D");
            trimestrales.Select(f => f.FechaVencimiento!.Value.DayNumber - d.DayNumber).Where(dias => dias <= 30).Should().BeEmpty(
                $"{tenant.Clave}: un trimestral recién emitido sigue Vigente el día de la demostración");

            if (filas.Count == 0) continue;

            // Control positivo, por Tenant con documentos: había de las tres clases que mirar.
            trimestrales.Should().NotBeNull();
            sinCaducidadEnTotal += sinCaducidad.Count;
        }

        sinCaducidadEnTotal.Should().BeGreaterThan(0, "la información de riesgos de un Trabajador sigue sin caducidad: el estado no desaparece de la siembra");
        conVencimientoAutomatico.Should().BeGreaterThan(100, "control: se miraron los documentos de los Tenants del piloto, no un conjunto vacío");
        fixture.Informe.Tenants.Should().OnlyContain(
            m => m.DocumentosConVencimientoAjenoASuTipo == 0 && m.DocumentosEmitidosEnElFuturo == 0, "la autoverificación midió lo mismo, por Tenant");
    }

    [Fact]
    public async Task Cada_documento_se_abre_como_PDF_y_los_pesados_pesan_entre_5_MB_y_10_MiB()
    {
        var tamanos = new List<(string Tenant, long Bytes)>();

        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(tenant.Nombre);
            await fixture.Arnes.EnTenantAsync(tenantId, async (db, sp) =>
            {
                var almacen = sp.GetRequiredService<IFileStorageService>();
                foreach (var clave in await db.Documentos.Select(d => d.ArchivoUrl).ToListAsync())
                {
                    clave.Should().NotBeNull("MEDIDO: ningún documento del piloto nace sin fichero");

                    await using var flujo = await almacen.AbrirAsync(clave!);
                    using var memoria = new MemoryStream();
                    await flujo.CopyToAsync(memoria);
                    memoria.Position = 0;

                    using var pdf = PdfReader.Open(memoria, PdfDocumentOpenMode.Import);
                    pdf.PageCount.Should().BeGreaterThan(0);
                    tamanos.Add((tenant.Clave, memoria.Length));
                }

                return 0;
            });
        }

        tamanos.Should().HaveCount(fixture.Resultado.Documentos, "MEDIDO: un PDF abierto por cada documento sembrado");
        var pesados = tamanos.Where(t => t.Bytes >= 5_000_000).ToList();
        salida.WriteLine($"MEDIDO PDF pesado: {string.Join(", ", pesados.Select(p => $"{p.Tenant} {p.Bytes} bytes"))}; el mayor de los demás: {tamanos.Where(t => t.Bytes < 5_000_000).Max(t => t.Bytes)} bytes");
        pesados.Select(p => p.Tenant).Distinct().Should().BeSubsetOf(["C"], "MEDIDO: el PDF pesado de «Evaluación de Riesgos Laborales» lo lleva solo el perfil multicliente");
        pesados.Should().OnlyContain(p => p.Bytes <= 10 * 1024 * 1024);
    }

    [Fact]
    public async Task Todos_los_contactos_de_agenda_llevan_una_variante_con_etiqueta_del_correo_configurado()
    {
        var correos = new List<string>();
        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            var tenantId = await fixture.Arnes.TenantIdAsync(tenant.Nombre);
            var delTenant = await fixture.Arnes.EnTenantAsync(tenantId, (db, _) => db.ContactosAgenda.Select(c => c.Email).ToListAsync());
            delTenant.Should().NotBeEmpty($"MEDIDO: {tenant.Clave} tiene a quién escribir");
            correos.AddRange(delTenant);
        }

        correos.Should().OnlyContain(c => c.StartsWith("ensayo+") && c.EndsWith("@destino.example"));
        correos.Should().OnlyHaveUniqueItems("MEDIDO: la etiqueta dice a qué contacto se escribió");
        salida.WriteLine($"MEDIDO contactos de agenda: {correos.Count}; ejemplo: {correos[0]}");
    }

    /// <summary>
    /// Lo que espera en el Tenant del Operador CAE externo, leído con las consultas de sus pantallas: la cuenta pendiente
    /// de activación en el equipo de la Coordinadora CAE. La solicitud de incorporación y la propuesta de apoyo (R14) no se
    /// siembran todavía: sus políticas exigen el Tenant de origen de una sesión autenticada.
    /// </summary>
    [Fact]
    public async Task El_Operador_CAE_externo_tiene_una_cuenta_pendiente_de_activacion()
    {
        var locales = CuentasPilotoOutbound.Locales;
        var operador = CatalogoPilotoOutbound.NombreTenantOperador;

        var equipo = await fixture.Arnes.ComoCuentaAsync(operador, locales.Coordinadora,
            sp => sp.GetRequiredService<ISender>().Send(new CaeManager.Application.Usuarios.ObtenerEquipoDeCoordinadorQuery()));

        salida.WriteLine($"MEDIDO equipo de la Coordinadora CAE: {string.Join(", ", equipo.Select(m => $"{m.NombreCompleto} (pendiente {m.PendienteActivacion})"))}");
        var pendiente = equipo.Should().ContainSingle(m => m.PendienteActivacion).Subject;
        (pendiente.Email, pendiente.NombreCompleto, pendiente.Activo).Should().Be(
            (locales.GestorPendienteDeActivacion, CatalogoPilotoOutbound.NombreGestorPendienteDeActivacion, true));
        equipo.Should().HaveCount(3, "control positivo: los dos Gestores CAE activos y el pendiente");

        var cuenta = await fixture.Arnes.ComoBootstrapAsync(b =>
            b.Users.IgnoreQueryFilters().SingleAsync(u => u.Email == locales.GestorPendienteDeActivacion));
        (cuenta.PasswordHash, cuenta.DebeCambiarContrasena).Should().Be(((string?)null, false), "MEDIDO: nace como el alta de la aplicación, sin contraseña");
        fixture.Arnes.Correo.Intentos.Should().BeEmpty("MEDIDO: sembrarla no genera enlace ni envía el correo de activación");

    }

    [Theory]
    [InlineData("administrador-del-operador", "*no tiene exactamente el rol GestorCae*")]
    [InlineData("administrador-de-T1", "*no es de ese Tenant*")]
    [InlineData("coordinadora-como-gestora", "*no tiene exactamente el rol GestorCae*")]
    [InlineData("gestora-como-coordinadora", "*no tiene exactamente el rol CoordinadorCae*")]
    [InlineData("cuenta-que-no-existe", "*no es de ese Tenant*")]
    public async Task La_medicion_no_construye_identidad_para_una_cuenta_que_no_es_la_de_su_papel(string caso, string mensaje)
    {
        var locales = CuentasPilotoOutbound.Locales;
        var cuentas = caso switch
        {
            "administrador-del-operador" => locales with { GestoraPrimera = locales.AdministradorOperador },
            "administrador-de-T1" => locales with { GestoraPrimera = locales.AdministradorT1 },
            "coordinadora-como-gestora" => locales with { GestoraPrimera = locales.Coordinadora },
            "gestora-como-coordinadora" => locales with { Coordinadora = locales.GestoraPrimera },
            _ => locales with { GestoraPrimera = "nadie@destino.example" },
        };

        var medicion = () => PilotoOutboundAutoverificacion.MedirAsync(
            fixture.Arnes.FabricaDeAmbitos, cuentas,
            new OpcionesPilotoOutbound(ArnesPilotoOutbound.FechaDemostracion(), ContactosPilotoOutbound.NoEntregables), CancellationToken.None);

        await medicion.Should().ThrowAsync<InvalidOperationException>().WithMessage(mensaje);
        fixture.Arnes.Servicios.GetRequiredService<IHttpContextAccessor>().HttpContext.Should().BeNull();
    }

    [Fact]
    public async Task Una_segunda_siembra_no_escribe_nada()
    {
        var antes = await fixture.Arnes.RecuentoAsync();
        antes["Tenants del piloto"].Should().Be(CatalogoPilotoOutbound.Tenants.Count + 1, "control: el recuento ve los once Tenants propietarios y el del Operador CAE externo");
        antes["Cuentas del piloto"].Should().Be(7, "MEDIDO: las seis del equipo (con la Dirección CAE) y de T1, y el Usuario de Cliente de T1");
        antes["Cuenta pendiente de activación"].Should().Be(1, "MEDIDO: la octava, fuera de las siete que tienen contraseña");
        (antes["Solicitudes de incorporación a cartera"], antes["Propuestas de apoyo"]).Should().Be((0, 0), "MEDIDO: no se siembran: sus políticas exigen el Tenant de origen de una sesión autenticada");
        antes["Ficheros en el almacén"].Should().Be(antes["Documentos"]);
        // Control positivo de las cuatro tablas que el recuento ve desde que cuenta Vehículos, Proyectos, verificaciones y
        // acreditaciones: una segunda siembra que duplicara cualquiera de ellas dejaba el recuento igual hasta entonces.
        // Las cifras salen de la matriz (A..G): 2+1+4+6+1+3+1 Vehículos, los 5 Proyectos de D y la Subcontrata Supervisada de F por sus tres tipos.
        (antes["Vehículos"], antes["Proyectos"], antes["Verificaciones externas de Subcontrata"])
            .Should().Be((31, 12, 3), "MEDIDO (A..H: 19, 8, 3; I, J y K suman 12 Vehículos y 4 Proyectos):Vehículos, Proyectos (con los tres eventos de H) y verificaciones externas del lote");
        antes["Acreditaciones externas"].Should().BeGreaterThan(0, "MEDIDO: las plataformas de A, B y C llevan acreditaciones");

        var segunda = await fixture.Arnes.SembrarAsync(fixture.Configuracion);

        segunda!.Escribio.Should().BeFalse();
        segunda.TenantsConDatosNuevos.Should().BeEmpty();
        (await fixture.Arnes.RecuentoAsync()).Should().BeEquivalentTo(antes, "MEDIDO: ni una fila, ni una cuenta, ni un fichero más");
        salida.WriteLine("MEDIDO recuento: " + string.Join(", ", antes.Select(p => $"{p.Key} {p.Value}")));
    }

    /// <summary>
    /// Decisión de Chris (2026-10-16, opción A): cada Tenant beneficiario sin Administrador propio —los siete que no
    /// son C— lleva un Encargo de administración vigente a favor del Operador CAE externo, sembrado por la siembra
    /// sin quien lo registre. C, con Administrador propio, no lleva ninguno. Y el Administrador del Operador CAE
    /// externo tiene en los ocho la cartera del Tenant entero con rol Coordinador CAE, sin marca de principal, que es
    /// lo que el encargo eleva. Se lee con la identidad de bootstrap y se afirma por nombre contra el catálogo.
    /// </summary>
    [Fact]
    public async Task Los_siete_Tenants_sin_Administrador_propio_llevan_el_encargo_de_la_siembra_y_C_no()
    {
        var conEncargo = CatalogoPilotoOutbound.Tenants.Where(t => !ReferenceEquals(t, C)).Select(t => t.Nombre).ToList();
        conEncargo.Should().HaveCount(CatalogoPilotoOutbound.Tenants.Count - 1);

        var medido = await fixture.Arnes.ComoBootstrapAsync(async b =>
        {
            var operadorId = await b.Tenants.Where(t => t.Nombre == CatalogoPilotoOutbound.NombreTenantOperador).Select(t => t.Id).SingleAsync();
            var administradorId = await b.Users.IgnoreQueryFilters()
                .Where(u => u.Email == CuentasPilotoOutbound.Locales.AdministradorOperador).Select(u => u.Id).SingleAsync();
            var nombres = CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre).ToList();
            var tenants = await b.Tenants.Where(t => nombres.Contains(t.Nombre)).ToDictionaryAsync(t => t.Id, t => t.Nombre);

            var encargos = await b.EncargosAdministracion.IgnoreQueryFilters()
                .Where(e => tenants.Keys.Contains(e.PropietarioTenantId)).ToListAsync();
            var carteras = await (
                from cartera in b.AsignacionesCartera.IgnoreQueryFilters()
                join operacion in b.AsignacionesOperacion.IgnoreQueryFilters() on cartera.AsignacionOperacionId equals operacion.Id
                where cartera.UsuarioId == administradorId && tenants.Keys.Contains(operacion.PropietarioTenantId)
                select new { Tenant = operacion.PropietarioTenantId, operacion.OperadorTenantId, cartera }).ToListAsync();

            return (operadorId, tenants, encargos, carteras: carteras.Select(c => (c.Tenant, c.OperadorTenantId, c.cartera)).ToList());
        });

        var ahora = DateTime.UtcNow;
        medido.encargos.Select(e => medido.tenants[e.PropietarioTenantId]).Should().BeEquivalentTo(conEncargo,
            "uno por Tenant sin Administrador propio, y ninguno en C");
        medido.encargos.Should().AllSatisfy(e =>
        {
            e.EstaVigente(ahora).Should().BeTrue();
            e.OperadorTenantId.Should().Be(medido.operadorId);
            e.Origen.Should().Be(OrigenEncargoAdministracion.SiembraDeDemostracion);
            e.RegistradoPorUsuarioId.Should().BeNull("no lo registró nadie: no se atribuye a una persona");
            e.ClausulaContrato.Should().Be(PilotoOutboundSeeder.ClausulaEncargoDeDemostracion);
        });

        medido.carteras.Select(c => medido.tenants[c.Tenant]).Should().BeEquivalentTo(
            CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre), "una cartera en cada uno de los once, también en C");
        medido.carteras.Should().AllSatisfy(c =>
        {
            c.OperadorTenantId.Should().Be(medido.operadorId, "cuelga de la operación externa del Operador CAE externo");
            c.cartera.Rol.Should().Be(Roles.CoordinadorCae, "la Operación nunca concede un rol de Propiedad");
            c.cartera.Ambito.EsUniversal.Should().BeTrue("del Tenant entero: si no, el encargo no eleva");
            c.cartera.EsPrincipal.Should().BeFalse("el principal sigue siendo del Gestor CAE");
            c.cartera.EstaVigenteEn(ahora).Should().BeTrue();
        });
    }

    /// <summary>
    /// Decisión Chris 2026-10-20, con la identidad real y por el camino de producción (<c>CurrentUserService</c>,
    /// bajo el rol restringido): el Administrador del Operador CAE externo es Administrador en todos los Tenants
    /// beneficiarios que su organización opera por una Asignación de Operación vigente del Tenant entero —la siembra
    /// la deja en los ocho—, tenga o no Encargo de administración y tenga o no el Tenant Administrador propio (C). El
    /// encargo ya no eleva, así que no hay encargo pleno que lo señale, y el acto que solo existe «por encargo»
    /// (<c>ActivarTratamientoIaPorEncargoCommand</c>) se niega en los dos: deuda declarada, la maquinaria del encargo
    /// queda sin efecto hasta que se retire o se reasigne.
    /// </summary>
    [Fact]
    public async Task El_Administrador_del_Operador_CAE_es_Administrador_en_todos_los_Tenants_operados_con_o_sin_encargo()
    {
        var conEncargo = CatalogoPilotoOutbound.Tenants.First(t => !ReferenceEquals(t, C));

        foreach (var tenant in new[] { conEncargo, C })
        {
            var medido = await ComoAdministradorDelOperadorEnAsync(tenant);
            medido.Rol.Should().Be(Roles.Administrador, $"{tenant.Nombre}: rige el perfil de origen, sin encargo");
            medido.EncargoPleno.Should().BeNull($"{tenant.Nombre}: el encargo ya no eleva el rol");
            medido.Activar.Error.Should().Be(ActivarTratamientoIaPorEncargoCommandHandler.NoAutorizado,
                $"{tenant.Nombre}: sin encargo pleno que lo señale, el acto por encargo no tiene autoridad (deuda)");
        }
    }

    private async Task<(string? Rol, Guid? EncargoPleno, Result<Guid> Activar)> ComoAdministradorDelOperadorEnAsync(
        TenantPilotoOutbound tenant)
    {
        var tenantId = await fixture.Arnes.TenantIdAsync(tenant.Nombre);
        return await fixture.Arnes.ComoCuentaAsync(
            CatalogoPilotoOutbound.NombreTenantOperador, CuentasPilotoOutbound.Locales.AdministradorOperador, async sp =>
            {
                using (AmbitoTenantExplicito.Establecer(tenantId))
                {
                    var usuario = sp.GetRequiredService<ICurrentUserService>();
                    return (await usuario.ObtenerRolEfectivoAsync(), await sp.GetRequiredService<IEncargoDeAdministracionActual>().EncargoPlenoAsync(),
                        await sp.GetRequiredService<ISender>().Send(
                            new ActivarTratamientoIaPorEncargoCommand("DPA-2026-10", "Anexo-II-2026-10")));
                }
            });
    }

    private sealed class RelojEn(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }
}

internal static class PilotoOutboundTextos
{
    public static string Linea(PilotoOutboundAutoverificacion.MedicionTenant m) =>
        $"{m.Clave}: Mi trabajo filas={m.MiTrabajoFilas} (bloqueos {m.MiTrabajoBloqueos}, actuaciones {m.MiTrabajoActuaciones}, próximos {m.MiTrabajoProximos}, " +
        $"seguimiento {m.MiTrabajoSeguimiento}) | Inicio {m.InicioCumplimiento}% vencidos={m.InicioVencidos} urgentes={m.InicioUrgentes} " +
        $"próximos={m.InicioProximos} sinConfirmar={m.InicioSinConfirmar} centrosBloqueados={m.InicioCentrosBloqueados} " +
        $"trabajadoresBloqueados={m.InicioTrabajadoresBloqueados} visitasUrgentes={m.InicioVisitasUrgentes} | Visión de cartera {m.VisionCarteraCumplimiento}% | " +
        $"Empresa {m.EmpresaCumplimiento}% | Centros [{string.Join("; ", m.Centros.Select(c => $"{c.Estado} {c.Cumplimiento}%"))}] | " +
        $"pares {m.ParesExigidos} (faltantes {m.ParesFaltantes}) | documentos {m.Documentos} (sin PDF {m.DocumentosSinPdf}) | agenda {m.ContactosDeAgenda}";

    /// <summary>Los Centros de Trabajo del Tenant por estado, con los seis estados aunque alguno no tenga ninguno.</summary>
    public static string CentrosPorEstado(PilotoOutboundAutoverificacion.MedicionTenant m) =>
        $"Centros por estado [{string.Join(", ", Enum.GetValues<EstadoCentro>().Select(e => $"{e} {m.Centros.Count(c => c.Estado == e)}"))}]";
}

/// <summary>
/// Sensibilidad de la autoverificación sobre datos que dejan de ser los del diseño,
/// y que medir no escribe: su propia base, porque la estropea.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class PilotoOutboundSensibilidadTests(ITestOutputHelper salida)
{
    [Fact]
    public async Task Medir_no_escribe_y_Exigir_nombra_el_Tenant_cuando_los_datos_se_apartan_del_diseno()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var configuracion = ArnesPilotoOutbound.Configurar(
            ArnesPilotoOutbound.FechaDemostracion(), extra: (OpcionesPilotoOutbound.ClaveDominioContactos, "contactos.example"));
        await arnes.SembrarAsync(configuracion);
        await arnes.BackfillAsync();

        // 1. Medir no escribe: ni filas del piloto, ni auditoría, ni cuentas tocadas.
        var filasAntes = await arnes.RecuentoAsync();
        var huellaAntes = await HuellaDeEscriturasAsync(arnes);

        var informe = await arnes.MedirAsync(configuracion);

        PilotoOutboundAutoverificacion.Exigir(informe);
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(filasAntes);
        (await HuellaDeEscriturasAsync(arnes)).Should().Be(huellaAntes, "MEDIDO: la medición no deja auditoría ni toca las cuentas con las que mide");
        arnes.Servicios.GetRequiredService<IHttpContextAccessor>().HttpContext.Should().BeNull("MEDIDO: la identidad se retira al terminar");

        var a = CatalogoPilotoOutbound.A;
        var idA = await arnes.TenantIdAsync(a.Nombre);

        // 1 bis. Por qué A no lleva el certificado de la Seguridad Social ni el ITA de su Empresa propia, medido. Un documento
        // mensual de la Empresa —emitido nueve días antes de hoy, como el más reciente que la siembra daría— vence antes de 30
        // días, «Próximo», y la causa de Empresa tiñe TODOS los Centros de esa Empresa, aunque el
        // Centro no exija el tipo (AgregarCausasDeEmpresaAsync no mira los requisitos del Centro). Con estos dos documentos A
        // dejaría de tener ningún Centro «Vigente» (el primero pasa a «Próximo»); se retiran (borrado lógico) y se comprueba que A vuelve a su estado.
        var vigentesDeAAntes = informe.De(a).Centros.Count(c => c.Estado == EstadoCentro.Vigente);
        vigentesDeAAntes.Should().BeGreaterThan(0, "control: A tiene Centros «Vigente» antes de tocar nada");
        var certificados = new[] { "Certificado de estar al corriente con la Seguridad Social", "ITA" };
        var sembradosParaElDiagnostico = await arnes.EnTenantAsync(idA, async (db, _) =>
        {
            var propia = await db.Empresas.SingleAsync(e => e.EsPropia);
            var emision = DiaDeNegocio.Hoy().AddDays(-9);
            var creados = new List<Documento>();
            foreach (var nombre in certificados)
            {
                var tipo = await db.TiposDocumento.SingleAsync(t => t.Nombre == nombre);
                tipo.VigenciaMeses.Should().Be(1, $"control: «{nombre}» es mensual");
                var vence = CalculadoraEstadoDocumento.CalcularFechaVencimiento(emision, tipo.VigenciaMeses);
                var documento = Documento.DeEmpresa(propia.Id, tipo.Id, emision, VigenciaDocumento.VenceEl(vence!.Value), archivoUrl: null);
                db.Documentos.Add(documento);
                creados.Add(documento);
            }

            await db.SaveChangesAsync();
            return creados.Select(c => c.Id).ToList();
        });
        var conLosCertificados = (await arnes.MedirAsync(configuracion)).De(a);
        salida.WriteLine("MEDIDO A con los dos certificados mensuales: " + PilotoOutboundTextos.CentrosPorEstado(conLosCertificados));
        conLosCertificados.Centros.Count(c => c.Estado == EstadoCentro.Vigente).Should().Be(
            0, "MEDIDO: un documento mensual de la Empresa propia —«Próximo» por su propio tipo— deja a todos sus Centros sin «Vigente»");
        conLosCertificados.Centros.Select(c => c.Estado).Should().BeEquivalentTo([EstadoCentro.Proximo, EstadoCentro.Pendiente], "MEDIDO: el Centro que era «Vigente» pasa a «Próximo» y el que ya esperaba una acreditación sigue «Pendiente»");
        await arnes.EnTenantAsync(idA, async (db, _) =>
        {
            foreach (var id in sembradosParaElDiagnostico)
                (await db.Documentos.SingleAsync(d => d.Id == id)).MarcarComoEliminado(Guid.NewGuid());
            return await db.SaveChangesAsync();
        });
        (await arnes.MedirAsync(configuracion)).De(a).Centros.Count(c => c.Estado == EstadoCentro.Vigente)
            .Should().Be(vigentesDeAAntes, "restauración verificada: sin los dos certificados A vuelve a tener sus Centros «Vigente»");

        // 2. Un documento de Trabajador del perfil A pasa a estar vencido: solo A se aparta del diseño.
        await arnes.EnTenantAsync(idA, async (db, _) =>
        {
            var documento = await db.Documentos.Where(d => d.TrabajadorId != null && d.FechaVencimiento != null).OrderBy(d => d.Id).FirstAsync();
            await CorregirComoElProductoAsync(db, documento, DiaDeNegocio.Hoy().AddDays(-20));
            return await db.SaveChangesAsync();
        });

        (await HuellaDeEscriturasAsync(arnes)).Should().NotBe(huellaAntes, "control positivo: la huella sí ve una escritura de verdad");

        var discrepancias = PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion));
        foreach (var linea in discrepancias) salida.WriteLine("MEDIDO " + linea);

        var prefijo = $"{a.Clave} «{a.Nombre}» · ";
        discrepancias.Should().NotBeEmpty("MEDIDO: el documento vencido aparta al Tenant de su intervalo o de su cuenta de vencidos")
            .And.OnlyContain(l => l.StartsWith(prefijo), "MEDIDO: solo se estropeó A");

        var exigir = async () => PilotoOutboundAutoverificacion.Exigir(await arnes.MedirAsync(configuracion));
        (await exigir.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(prefijo);

        // 3. Una segunda Empresa propia en B rompe la invariante: la autoverificación lo nombra.
        var b = CatalogoPilotoOutbound.B;
        var idB = await arnes.TenantIdAsync(b.Nombre);
        await arnes.EnTenantAsync(idB, async (db, _) =>
        {
            var cliente = await db.Empresas.Where(e => !e.EsPropia).OrderBy(e => e.Id).FirstAsync();
            typeof(Empresa).GetProperty(nameof(Empresa.EsPropia))!.SetValue(cliente, true);
            return await db.SaveChangesAsync();
        });
        var conDosPropias = PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion));
        conDosPropias.Should().Contain(l => l.StartsWith($"{b.Clave} «{b.Nombre}» · ") && l.Contains("Empresas con EsPropia"),
            "MEDIDO: dos Empresas propias en un Tenant es una discrepancia nombrada");

        // 4. Un Trabajador de F con el alta en la Empresa DESPUÉS de su Formación Art. 19: la autoverificación lo nombra.
        var f = CatalogoPilotoOutbound.F;
        var idF = await arnes.TenantIdAsync(f.Nombre);
        await arnes.EnTenantAsync(idF, async (db, _) =>
        {
            var formacion = await db.TiposDocumento.Where(t => t.Nombre == CatalogoPilotoOutbound.FormacionArt19).Select(t => t.Id).SingleAsync();
            var trabajadorId = await db.Documentos.Where(d => d.TrabajadorId != null && d.TipoDocumentoId == formacion)
                .OrderBy(d => d.Id).Select(d => d.TrabajadorId!.Value).FirstAsync();
            var trabajador = await db.Trabajadores.SingleAsync(t => t.Id == trabajadorId);
            typeof(CaeManager.Domain.Trabajadores.Trabajador).GetProperty(nameof(CaeManager.Domain.Trabajadores.Trabajador.FechaAltaEmpresa))!
                .SetValue(trabajador, DiaDeNegocio.Hoy().AddDays(-1));
            return await db.SaveChangesAsync();
        });
        var conAltaPosterior = PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion));
        conAltaPosterior.Should().Contain(
            l => l.StartsWith($"{f.Clave} «{f.Nombre}» · ") && l.Contains("alta en la Empresa posterior") && l.Contains("medido 1, esperado 0"),
            "MEDIDO: un alta posterior a su formación es una discrepancia nombrada, y solo la de ese Trabajador");
    }

    /// <summary>
    /// Corrige un documento para que venza <paramref name="haciaElDia"/> —o hasta tres días antes, si ese día no lo
    /// alcanza ninguna emisión— con la emisión de la que el producto sacaría ese vencimiento: así la alteración
    /// solo aparta de la matriz el estado, y no también la coherencia de las fechas con el Tipo.
    /// </summary>
    private static async Task CorregirComoElProductoAsync(
        CaeManager.Infrastructure.Persistence.CaeManagerDbContext db, Documento documento, DateOnly haciaElDia)
    {
        var meses = await db.TiposDocumento.Where(t => t.Id == documento.TipoDocumentoId).Select(t => t.VigenciaMeses).SingleAsync() ?? 12;
        var emision = haciaElDia.AddMonths(-meses);
        documento.CorregirVigencia(emision, VigenciaDocumento.VenceEl(emision.AddMonths(meses)));
    }

    /// <summary>Filas de auditoría de toda la base y sellos de concurrencia de las cuentas: cambia con cualquier escritura por la aplicación.</summary>
    private static Task<string> HuellaDeEscriturasAsync(ArnesPilotoOutbound arnes) =>
        arnes.ComoBootstrapAsync(async b =>
        {
            var auditoria = await b.RegistrosAuditoria.IgnoreQueryFilters().CountAsync();
            var sellos = await b.Users.IgnoreQueryFilters().OrderBy(u => u.Id).Select(u => u.ConcurrencyStamp + "/" + u.SecurityStamp).ToListAsync();
            return $"{auditoria}|{string.Join(",", sellos)}";
        });
}

/// <summary>Lo que la siembra se niega a hacer, sobre una base en la que nunca llega a escribir.</summary>
public sealed class PilotoOutboundSinSiembraFixture : IAsyncLifetime
{
    internal ArnesPilotoOutbound Arnes { get; private set; } = null!;

    public async Task InitializeAsync() => Arnes = await ArnesPilotoOutbound.CrearAsync();

    public async Task DisposeAsync() => await Arnes.DisposeAsync();
}

[Collection(ColeccionPilotoOutbound.Nombre)]
public class PilotoOutboundNoEscribeTests(PilotoOutboundSinSiembraFixture fixture) : IClassFixture<PilotoOutboundSinSiembraFixture>
{
    private async Task NoHaNacidoNadaAsync()
    {
        var recuento = await fixture.Arnes.RecuentoAsync();
        recuento["Tenants del piloto"].Should().Be(0, "MEDIDO: el rechazo es previo a cualquier escritura");
        recuento["Cuentas del piloto"].Should().Be(0);
        recuento["Ficheros en el almacén"].Should().Be(0);
        recuento["Tenants en total"].Should().BeGreaterThan(0, "control positivo: el recuento ve los Tenants de la base");
    }

    [Fact]
    public async Task Sin_su_clave_no_siembra_nada_aunque_DatosPrueba_este_activo()
    {
        var resultado = await fixture.Arnes.SembrarAsync(ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion(), activo: false));

        resultado.Should().BeNull("MEDIDO: inerte por defecto");
        await NoHaNacidoNadaAsync();
    }

    [Fact]
    public async Task En_Produccion_lanza_con_su_propio_mensaje_y_no_siembra_nada()
    {
        var siembra = () => fixture.Arnes.SembrarAsync(
            ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion()), new EntornoDePrueba("Production"));

        await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage(
            "*PilotoOutbound:Activo no puede activarse en Producción*",
            "MEDIDO: la guarda es la de ESTA siembra; CredencialesDemo lanza otra que también dice «Producción»");
        await NoHaNacidoNadaAsync();
    }

    [Theory]
    [InlineData("Production", true, true, true)]
    [InlineData("Production", true, false, false)]
    [InlineData("Production", false, true, false)]
    [InlineData("Development", true, true, false)]
    [InlineData("Staging", true, true, false)]
    public void La_guarda_de_Produccion_rechaza_solo_con_las_dos_claves_activas_en_Produccion(
        string entorno, bool datosPrueba, bool piloto, bool debeLanzar)
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = datosPrueba.ToString(),
            [OpcionesPilotoOutbound.ClaveActivo] = piloto.ToString(),
        }).Build();

        var llamada = () => PilotoOutboundSeeder.RechazarEnProduccion(configuracion, new EntornoDePrueba(entorno));

        if (debeLanzar)
            llamada.Should().Throw<InvalidOperationException>().WithMessage("*PilotoOutbound:Activo no puede activarse en Producción*");
        else
            llamada.Should().NotThrow();
    }

    [Fact]
    public void El_arranque_rechaza_la_clave_en_Produccion_antes_de_cualquier_otra_siembra()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        directorio.Should().NotBeNull("el instrumento tiene que localizar la raíz del repositorio para leer Program.cs");

        var programa = File.ReadAllText(Path.Combine(directorio!.FullName, "src", "CaeManager.Web", "Program.cs"));
        var rechazo = programa.IndexOf("PilotoOutboundSeeder.RechazarEnProduccion(", StringComparison.Ordinal);
        var primeraSiembra = programa.IndexOf("IdentitySeeder.SeedAsync(", StringComparison.Ordinal);

        rechazo.Should().BeGreaterThan(-1, "el arranque tiene que invocar la guarda");
        primeraSiembra.Should().BeGreaterThan(-1, "control positivo: el instrumento ve la primera siembra del arranque");
        rechazo.Should().BeLessThan(primeraSiembra, "con la clave activa en Producción, las siembras anteriores ya habrían escrito");
    }

    [Theory]
    [InlineData(null, "*Falta DatosPrueba:PilotoOutbound:FechaDemostracion*")]
    [InlineData("14/10/2026", "*no es una fecha con formato yyyy-MM-dd*")]
    [InlineData("-1", "*es anterior a hoy*")]
    [InlineData("+10", "*queda a más de 9 días de hoy*")]
    public async Task Con_la_fecha_de_demostracion_ausente_ilegible_pasada_o_lejana_se_niega_antes_de_escribir(string? fecha, string mensaje)
    {
        var valor = fecha is not null && (fecha[0] is '-' or '+')
            ? DiaDeNegocio.Hoy().AddDays(int.Parse(fecha)).ToString("yyyy-MM-dd")
            : fecha;
        var configuracion = ArnesPilotoOutbound.Configurar(null, extra: (OpcionesPilotoOutbound.ClaveFechaDemostracion, valor));

        var siembra = () => fixture.Arnes.SembrarAsync(configuracion);

        await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage(mensaje);
        await NoHaNacidoNadaAsync();
    }

    [Theory]
    [InlineData("CorreoContactos", "ensayo+yo@destino.example", "*CorreoContactos no es una dirección utilizable*")]
    [InlineData("CorreoContactos", "sin-arroba", "*CorreoContactos no es una dirección utilizable*")]
    public async Task Con_un_correo_de_contactos_que_no_vale_se_niega_antes_de_escribir(string clave, string valor, string mensaje)
    {
        var configuracion = ArnesPilotoOutbound.Configurar(
            ArnesPilotoOutbound.FechaDemostracion(), extra: ($"{OpcionesPilotoOutbound.Seccion}:{clave}", valor));

        var siembra = () => fixture.Arnes.SembrarAsync(configuracion);

        await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage(mensaje);
        await NoHaNacidoNadaAsync();
    }

    [Fact]
    public void En_el_limite_de_nueve_dias_y_en_el_propio_dia_la_fecha_vale()
    {
        var hoy = new DateOnly(2026, 10, 9);

        var enElLimite = () => OpcionesPilotoOutbound.ValidarFecha(hoy.AddDays(9), hoy);
        var hoyMismo = () => OpcionesPilotoOutbound.ValidarFecha(hoy, hoy);

        enElLimite.Should().NotThrow();
        hoyMismo.Should().NotThrow();
        OpcionesPilotoOutbound.MotivoFechaNoUtilizable(hoy.AddDays(9), hoy).Should().BeNull();
        OpcionesPilotoOutbound.MotivoFechaNoUtilizable(hoy.AddDays(-1), hoy).Should().Contain("es anterior a hoy");
        OpcionesPilotoOutbound.MotivoFechaNoUtilizable(hoy.AddDays(10), hoy).Should().Contain("queda a más de 9 días de hoy");
    }

    [Fact]
    public void Las_fechas_de_la_siembra_respetan_los_margenes_de_la_matriz_para_cualquier_indice()
    {
        var d = new DateOnly(2026, 10, 14);
        var fechas = new FechasPilotoOutbound(d);

        foreach (var i in Enumerable.Range(0, 2_000))
        {
            fechas.Vigente(i).Should().BeAfter(d.AddDays(60));
            fechas.Proximo(i).Should().BeOnOrAfter(d.AddDays(20)).And.BeOnOrBefore(d.AddDays(25));
            fechas.Urgente(i).Should().BeOnOrAfter(d.AddDays(5)).And.BeOnOrBefore(d.AddDays(10));
            fechas.Vencido(i).Should().BeOnOrAfter(d.AddDays(-90)).And.BeOnOrBefore(d.AddDays(-10));
            fechas.Emision(i).Should().BeBefore(d.AddDays(-30));
        }
    }

    /// <summary>
    /// El estado se calcula contra hoy, y hoy puede ser cualquier día entre D−9 y D. Con los umbrales
    /// por defecto (ámbar 30, rojo 15), cada fecha tiene que dar el mismo estado los diez días.
    /// </summary>
    [Fact]
    public void Las_fechas_de_la_siembra_dan_el_mismo_estado_cualquier_dia_en_que_se_pueda_sembrar()
    {
        var d = new DateOnly(2026, 10, 14);
        var fechas = new FechasPilotoOutbound(d);
        EstadoDocumento Estado(DateOnly vence, DateOnly hoy) => CalculadoraEstadoDocumento.Calcular(
            VigenciaDocumento.VenceEl(vence), hoy, umbralAmbarDias: 30, umbralRojoDias: 15);

        Estado(d.AddDays(22), d.AddDays(-9)).Should().Be(EstadoDocumento.Vigente, "control positivo: una fecha del margen ancho de «Próximo» cambia de estado dentro de la ventana");
        Estado(d.AddDays(22), d).Should().Be(EstadoDocumento.Proximo);

        foreach (var hoy in Enumerable.Range(0, OpcionesPilotoOutbound.MargenMaximoDias + 1).Select(n => d.AddDays(-n)))
        {
            foreach (var i in Enumerable.Range(0, 300))
            {
                Estado(fechas.Vigente(i), hoy).Should().Be(EstadoDocumento.Vigente);
                Estado(fechas.Proximo(i), hoy).Should().Be(EstadoDocumento.Proximo);
                Estado(fechas.Urgente(i), hoy).Should().Be(EstadoDocumento.Urgente);
                Estado(fechas.Vencido(i), hoy).Should().Be(EstadoDocumento.Vencido);
            }
        }
    }

    [Fact]
    public void La_regla_de_correo_de_los_contactos_distingue_el_buzon_configurado_del_dominio_no_entregable()
    {
        var conCorreo = ContactosPilotoOutbound.Crear(ArnesPilotoOutbound.CorreoDePrueba, null);
        conCorreo.DireccionDe("cae.aldrevia").Should().Be("ensayo+cae.aldrevia@destino.example");
        conCorreo.Cumple("ensayo+cae.aldrevia@destino.example").Should().BeTrue();
        conCorreo.Cumple("ensayo@destino.example").Should().BeFalse("sin etiqueta no se sabe a qué contacto se escribió");
        conCorreo.Cumple("otra+cae.aldrevia@destino.example").Should().BeFalse();
        conCorreo.Cumple("cae.aldrevia@caemanager.local").Should().BeFalse();

        var sinCorreo = ContactosPilotoOutbound.Crear(" ", null);
        sinCorreo.Should().Be(ContactosPilotoOutbound.NoEntregables);
        sinCorreo.DireccionDe("cae.aldrevia").Should().Be("cae.aldrevia@caemanager.local");
        sinCorreo.Cumple("ensayo+cae.aldrevia@destino.example").Should().BeFalse();
    }
}

/// <summary>
/// Una siembra cortada a mitad: lo que deja, que se puede reanudar sin retirar,
/// que la retirada lo limpia todo y que una siembra nueva se completa como una
/// primera. Y el nombre del piloto ocupado por un Tenant que no es de demo.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class PilotoOutboundInterrupcionTests(ITestOutputHelper salida)
{
    // El orden de siembra es E, B, G, A, F, D, C: el almacén falla en el décimo PDF de A, con E, B y G ya guardados.
    private static readonly string[] AntesDeA = [CatalogoPilotoOutbound.NombreTenantE, CatalogoPilotoOutbound.NombreTenantB, CatalogoPilotoOutbound.NombreTenantG];

    private static IEnumerable<string> Vigentes =>
        CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre).Append(CatalogoPilotoOutbound.NombreTenantOperador);

    private static Task<Dictionary<string, int>> DocumentosPorTenantAsync(ArnesPilotoOutbound arnes) =>
        arnes.ComoBootstrapAsync(async b =>
        {
            var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
            var nombrePorId = await b.Tenants.Where(t => nombres.Contains(t.Nombre)).ToDictionaryAsync(t => t.Id, t => t.Nombre);
            var ids = nombrePorId.Keys.ToList();
            var filas = await b.Documentos.IgnoreQueryFilters().Where(d => ids.Contains(d.TenantId)).GroupBy(d => d.TenantId)
                .Select(g => new { g.Key, N = g.Count() }).ToListAsync();
            return filas.ToDictionary(f => nombrePorId[f.Key], f => f.N);
        });

    [Fact]
    public async Task Una_siembra_cortada_limpia_los_PDF_de_su_Tenant_se_reanuda_se_retira_entera_y_se_vuelve_a_sembrar()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var configuracion = ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion());
        var sinPiloto = await arnes.RecuentoAsync();

        // 0. Una siembra entera mide cuántos documentos lleva cada Tenant; se retira y la base vuelve a como estaba.
        (await arnes.SembrarAsync(configuracion))!.Escribio.Should().BeTrue("control: la primera siembra escribe");
        var porTenant = await DocumentosPorTenantAsync(arnes);
        salida.WriteLine("MEDIDO documentos por Tenant: " + string.Join(", ", porTenant.Select(p => $"{p.Key} {p.Value}")));
        var pdfAntesDeA = AntesDeA.Sum(n => porTenant[n]);
        var pdfEnElQueFalla = pdfAntesDeA + 10;
        porTenant[CatalogoPilotoOutbound.NombreTenantA].Should().BeGreaterThan(10, "control: A tiene más de diez documentos, así que el fallo cae dentro de A");
        await arnes.RetirarAsync();
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(sinPiloto, "control: la retirada devuelve la base a su estado anterior");

        // 1. El almacén falla en mitad de A.
        var conFallo = () => arnes.SembrarAsync(configuracion, envolverAlmacen: real => new AlmacenQueSeCorta(real, pdfEnElQueFalla));
        await conFallo.Should().ThrowAsync<IOException>().WithMessage("*disco lleno de mentira*");

        var trasElFallo = await arnes.RecuentoAsync();
        salida.WriteLine("MEDIDO tras el fallo del almacén: " + Texto(trasElFallo));
        trasElFallo["Ficheros en el almacén"].Should().Be(pdfAntesDeA, "MEDIDO: los nueve PDF que A ya había escrito se eliminan; quedan los de E, B y G, que sí se guardaron");
        trasElFallo["Documentos"].Should().Be(pdfAntesDeA);
        (await TenantsSinMarcadorAsync(arnes)).Should().BeEmpty("MEDIDO: todo Tenant del piloto lleva el marcador desde que nace");

        // 1 bis. Con la fecha de la demostración ya pasada y A a medias, la siembra se niega: queda algo por escribir.
        var conLaFechaPasada = () => arnes.SembrarAsync(ArnesPilotoOutbound.Configurar(DiaDeNegocio.Hoy().AddDays(-1)));
        await conLaFechaPasada.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*es anterior a hoy*Queda por sembrar «{CatalogoPilotoOutbound.NombreTenantA}»*");
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(trasElFallo, "MEDIDO: el rechazo no escribe");

        // 2. Reanudar sin retirar: se completa lo que faltaba y la matriz sale.
        var reanudada = await arnes.SembrarAsync(configuracion);
        reanudada!.TenantsConDatosNuevos.Should().Equal(
            CatalogoPilotoOutbound.NombreTenantA, CatalogoPilotoOutbound.NombreTenantF, CatalogoPilotoOutbound.NombreTenantH,
            CatalogoPilotoOutbound.NombreTenantD, CatalogoPilotoOutbound.NombreTenantK, CatalogoPilotoOutbound.NombreTenantJ,
            CatalogoPilotoOutbound.NombreTenantC, CatalogoPilotoOutbound.NombreTenantI);
        await arnes.BackfillAsync();
        PilotoOutboundAutoverificacion.Exigir(await arnes.MedirAsync(configuracion));
        var completa = await arnes.RecuentoAsync();
        completa["Ficheros en el almacén"].Should().Be(porTenant.Values.Sum());
        var escenariosDelOperador = new[] { "Cuenta pendiente de activación",
            "Revisiones de IA", "Detecciones de Trabajador", "Sugerencias de Visita" };
        var conSenales = CatalogoPilotoOutbound.Tenants.Count(t => t.ConSenalesDeIa);
        escenariosDelOperador.Select(k => completa[k]).Should().Equal([1, conSenales, conSenales, conSenales], "control positivo: la siembra completa los tiene (las señales, en C y en el escaparate)");

        // 3. La retirada no deja nada del piloto y no toca lo demás.
        (await arnes.RetirarAsync()).Should().HaveCount(CatalogoPilotoOutbound.Tenants.Count + 1);
        var retirada = await arnes.RecuentoAsync();
        retirada.Should().BeEquivalentTo(sinPiloto, "MEDIDO: ni Tenants, ni cuentas, ni filas, ni ficheros del piloto; lo demás, igual que antes");
        escenariosDelOperador.Select(k => retirada[k]).Should().Equal([0, 0, 0, 0], "MEDIDO: la retirada borra la cuenta pendiente y las tres señales de IA y correo");
        sinPiloto["Tenants en total"].Should().BeGreaterThan(0, "control positivo: había otros Tenants que no debían tocarse");

        // 4. Cancelación en mitad de A.
        using var cancelacion = new CancellationTokenSource();
        var cancelada = () => arnes.SembrarAsync(
            configuracion, envolverAlmacen: real => new AlmacenQueSeCorta(real, pdfEnElQueFalla, cancelacion.Cancel), cancellationToken: cancelacion.Token);
        await cancelada.Should().ThrowAsync<OperationCanceledException>();

        var trasCancelar = await arnes.RecuentoAsync();
        salida.WriteLine("MEDIDO tras la cancelación: " + Texto(trasCancelar));
        trasCancelar["Ficheros en el almacén"].Should().Be(pdfAntesDeA, "MEDIDO: la limpieza corre aunque la causa sea la cancelación");

        // 5. Retirar lo que dejó la siembra cortada, y sembrar de nuevo como la primera vez.
        await arnes.RetirarAsync();
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(sinPiloto);

        var nueva = await arnes.SembrarAsync(configuracion);
        nueva!.TenantsConDatosNuevos.Should().Equal(CatalogoPilotoOutbound.EnOrdenDeSiembra.Select(t => t.Nombre));
        await arnes.BackfillAsync();
        PilotoOutboundAutoverificacion.Exigir(await arnes.MedirAsync(configuracion));
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(completa, "MEDIDO: igual que la siembra completa anterior");
    }

    [Fact]
    public async Task Con_un_nombre_del_piloto_ocupado_por_un_Tenant_sin_marcador_de_demo_se_niega_y_lo_nombra()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();

        using (var ambito = arnes.Servicios.CreateScope())
            await DelegacionDemoSeeder.AprovisionarTenantAsync(
                ambito.ServiceProvider.GetRequiredService<CaeManager.Infrastructure.Persistence.CaeManagerDbContext>(),
                CatalogoPilotoOutbound.NombreTenantB, PerfilVocabularioTenant.ClienteDirecto, NullLogger.Instance,
                CancellationToken.None, esOperadorCaeExterno: false);

        var antes = await arnes.RecuentoAsync();
        antes["Tenants del piloto"].Should().Be(1, "control: el Tenant que ocupa el nombre existe");

        var siembra = () => arnes.SembrarAsync(ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion()));

        await siembra.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*«{CatalogoPilotoOutbound.NombreTenantB}»*NO lleva el marcador de datos de demo*");
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(antes, "MEDIDO: no escribe ni en ese Tenant ni a su lado");

        var retirada = () => arnes.RetirarAsync();
        await retirada.Should().ThrowAsync<InvalidOperationException>("la retirada tampoco toca un Tenant sin marcador");
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(antes);
    }

    /// <summary>
    /// El lote que sembró esta versión antes de que la siembra llevara la instrucción de tratamiento de IA: los
    /// ocho Tenants con todos sus datos y ninguno con instrucción. El asistente falla cerrado —es el estado que
    /// este paso corrige—, la resiembra añade las ocho instrucciones sin tocar nada más, repetirla no añade
    /// ninguna, y la retirada se las lleva.
    /// </summary>
    [Fact]
    public async Task A_un_lote_ya_sembrado_sin_instruccion_de_tratamiento_de_IA_la_resiembra_solo_le_anade_la_instruccion_y_la_retirada_la_borra()
    {
        const string Clave = "Instrucciones de tratamiento de IA";
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var configuracion = ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion());
        var sinPiloto = await arnes.RecuentoAsync();

        (await arnes.SembrarAsync(configuracion))!.Escribio.Should().BeTrue("control: la primera siembra escribe");
        await arnes.BackfillAsync();
        var completa = await arnes.RecuentoAsync();
        salida.WriteLine("MEDIDO lote completo: " + Texto(completa));

        // 1. Qué deja la siembra: una por Tenant, vigente, con las versiones Draft de la demo y atribuida a una cuenta del lote.
        var administradorDelOperador = await arnes.ComoBootstrapAsync(b => b.Users.IgnoreQueryFilters()
            .Where(u => u.Email == CuentasPilotoOutbound.Locales.AdministradorOperador).Select(u => u.Id).SingleAsync());
        var sembradas = await InstruccionesDelPilotoAsync(arnes);
        sembradas.Select(i => i.Tenant).Should().BeEquivalentTo(
            Vigentes, "MEDIDO: exactamente una por Tenant del piloto, el Operador CAE externo y los once propietarios");
        sembradas.Should().OnlyContain(i => i.Vigente, "MEDIDO: todas vigentes");
        sembradas.Should().OnlyContain(
            i => i.Dpa == "Draft-2026-09-03" && i.Anexo == "Draft-2026-09-03",
            "MEDIDO: las versiones son las Draft de la siembra de demo: un borrador, nunca una aceptación real");
        sembradas.Should().OnlyContain(i => i.Origen == OrigenInstruccionTratamientoIa.AltaManualPlataforma, "MEDIDO: el origen de la siembra de demo");
        sembradas.Should().OnlyContain(i => i.RegistradaPor == administradorDelOperador, "MEDIDO: la registra el Administrador del Operador CAE externo del lote");

        // 2. El lote tal como lo dejó la siembra anterior a este paso: sin ninguna instrucción.
        (await arnes.ComoBootstrapAsync(async b =>
        {
            var ids = await IdsDelPilotoAsync(b);
            return await b.InstruccionesTratamientoIaTenantPropietario.IgnoreQueryFilters().Where(i => ids.Contains(i.TenantId)).ExecuteDeleteAsync();
        })).Should().Be(CatalogoPilotoOutbound.Tenants.Count + 1, "control: se han quitado las doce");
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(
            new Dictionary<string, int>(completa) { [Clave] = 0 }, "control: lo único que cambia respecto del lote completo es que no hay instrucciones");

        // 3. Así, el modo Preguntar se niega antes de llamar al proveedor, y la autoverificación lo dice.
        var pregunta = new PreguntarAlAsistenteQuery([new MensajeChatDto(RolMensajeChat.Usuario, "¿Qué vence esta semana?")]);
        var negativa = await arnes.ComoGestoraPrimeraEnAsync(
            CatalogoPilotoOutbound.NombreTenantC, sp => sp.GetRequiredService<ISender>().Send(pregunta));
        negativa.EsFallido.Should().BeTrue("MEDIDO: sin instrucción en el Tenant en pantalla, el asistente no procesa el mensaje");
        negativa.Error.Codigo.Should().Be("AsistenteIa.SinInstruccion", "MEDIDO: y falla por la instrucción, no por otra cosa");

        var carteraSinInstruccion = await arnes.ComoGestoraPrimeraEnAsync(
            CatalogoPilotoOutbound.NombreTenantC, sp => sp.GetRequiredService<ISender>().Send(new ComprobarInstruccionIaCarteraQuery()));
        carteraSinInstruccion.SinInstruccion.Select(t => t.Nombre).Should().BeEquivalentTo(
            CatalogoPilotoOutbound.Tenants.Where(t => t.EnCarteraDeLaGestoraPrimera).Select(t => t.Nombre).Append(CatalogoPilotoOutbound.NombreTenantOperador),
            "MEDIDO: los seis Tenants de la cartera de la Gestora CAE están sin instrucción");
        carteraSinInstruccion.ErrorSiFalta()!.Codigo.Should().Be(InstruccionIaCarteraDto.CodigoError);

        var discrepancias = PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion));
        foreach (var linea in discrepancias) salida.WriteLine("MEDIDO " + linea);
        discrepancias.Should().HaveCount(26, "una por Tenant y por cuenta: ocho de la Gestora CAE (con I y K), seis del segundo Gestor CAE (con J) y doce de la Coordinadora CAE");
        discrepancias.Should().OnlyContain(l => l.StartsWith("Asistente IA · "), "control: del lote solo falla el Asistente IA");
        discrepancias.Should().Contain(
            $"Asistente IA · cartera de la Coordinadora CAE · «{CatalogoPilotoOutbound.NombreTenantC}»: medido sin instrucción de tratamiento de IA " +
            "vigente, esperado con instrucción de tratamiento de IA vigente.");

        // 4. La resiembra: no hay nada que sembrar salvo la instrucción.
        var resiembra = await arnes.SembrarAsync(configuracion);
        resiembra!.Escribio.Should().BeFalse("MEDIDO: añadir la instrucción que faltaba no cuenta como haber escrito el lote");
        resiembra.TenantsConDatosNuevos.Should().BeEmpty();
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(completa, "vuelve a haber una instrucción por Tenant del lote y nada más ha cambiado");
        (await InstruccionesDelPilotoAsync(arnes)).Select(i => i.Tenant).Should().BeEquivalentTo(
            Vigentes, "MEDIDO: una por Tenant, no nueve en uno");
        PilotoOutboundAutoverificacion.Exigir(await arnes.MedirAsync(configuracion));

        var carteraConInstruccion = await arnes.ComoCuentaAsync(
            CatalogoPilotoOutbound.NombreTenantOperador, CuentasPilotoOutbound.Locales.Coordinadora,
            sp => sp.GetRequiredService<ISender>().Send(new ComprobarInstruccionIaCarteraQuery()));
        carteraConInstruccion.ErrorSiFalta().Should().BeNull("MEDIDO: la comprobación de cartera pasa para la Coordinadora CAE");
        carteraConInstruccion.ConInstruccion.Should().HaveCount(CatalogoPilotoOutbound.Tenants.Count + 1, "control: y pasa con los doce en la cartera, no con una cartera vacía");

        // 5. Repetirla no añade ninguna.
        await arnes.SembrarAsync(configuracion);
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(completa, "MEDIDO: idempotente, siguen siendo nueve");

        // 6. Una instrucción revocada a propósito en un ensayo no se repone: el Tenant ya tiene la suya, cerrada.
        var t2Id = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantA);
        (await arnes.EnTenantAsync(t2Id, async (db, _) =>
        {
            (await db.InstruccionesTratamientoIaTenantPropietario.SingleAsync()).Revocar("Ensayo del fallo cerrado.", DateTime.UtcNow);
            return await db.SaveChangesAsync();
        })).Should().BeGreaterThan(0, "control: la revocación se guardó");
        await arnes.SembrarAsync(configuracion);
        var trasRevocar = await InstruccionesDelPilotoAsync(arnes);
        trasRevocar.Should().HaveCount(CatalogoPilotoOutbound.Tenants.Count + 1, "MEDIDO: la siembra no añade otra al Tenant cuya instrucción se revocó");
        trasRevocar.Where(i => !i.Vigente).Select(i => i.Tenant).Should().Equal(
            [CatalogoPilotoOutbound.NombreTenantA], "MEDIDO: y la revocada sigue revocada");
        PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion)).Should().HaveCount(2, "MEDIDO: la autoverificación lo dice, una vez por cuenta")
            .And.OnlyContain(l => l.StartsWith("Asistente IA · ") && l.Contains($"«{CatalogoPilotoOutbound.NombreTenantA}»: medido sin instrucción"));

        // 7. La retirada se las lleva con el resto del lote, también la revocada.
        completa[Clave].Should().Be(CatalogoPilotoOutbound.Tenants.Count + 1, "control positivo: antes de retirar hay doce que borrar");
        (await arnes.RetirarAsync()).Should().HaveCount(CatalogoPilotoOutbound.Tenants.Count + 1);
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(sinPiloto);
        // El recuento del arnés cuenta por los Tenants del piloto que existen, y ya no existe ninguno: daría cero aunque
        // las filas siguieran ahí. Lo que mide la retirada es este, sobre la tabla entera de una base recién creada.
        (await arnes.ComoBootstrapAsync(b => b.InstruccionesTratamientoIaTenantPropietario.IgnoreQueryFilters().CountAsync()))
            .Should().Be(0, "MEDIDO: la retirada no deja ninguna instrucción, tampoco huérfana de un Tenant que ya no existe");
    }

    private sealed record InstruccionSembrada(string Tenant, bool Vigente, string Dpa, string Anexo, OrigenInstruccionTratamientoIa Origen, Guid RegistradaPor);

    private static Task<List<Guid>> IdsDelPilotoAsync(CaeManager.Infrastructure.Persistence.CaeManagerDbContext bootstrap)
    {
        var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
        return bootstrap.Tenants.Where(t => nombres.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
    }

    /// <summary>Las instrucciones de los Tenants del piloto, también las revocadas, leídas con la identidad de bootstrap y sin filtros.</summary>
    private static Task<List<InstruccionSembrada>> InstruccionesDelPilotoAsync(ArnesPilotoOutbound arnes) =>
        arnes.ComoBootstrapAsync(async b =>
        {
            var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
            var nombrePorId = await b.Tenants.Where(t => nombres.Contains(t.Nombre)).ToDictionaryAsync(t => t.Id, t => t.Nombre);
            var ids = nombrePorId.Keys.ToList();
            var filas = await b.InstruccionesTratamientoIaTenantPropietario.IgnoreQueryFilters()
                .Where(i => ids.Contains(i.TenantId)).ToListAsync();

            return filas.Select(i => new InstruccionSembrada(
                nombrePorId[i.TenantId], i.EstaVigente, i.VersionDpaAceptada, i.VersionAnexoSubencargadosAceptada,
                i.OrigenInstruccion, i.RegistradaPorUsuarioId)).ToList();
        });

    private static string Texto(Dictionary<string, int> recuento) => string.Join(", ", recuento.Select(p => $"{p.Key} {p.Value}"));

    private static Task<List<string>> TenantsSinMarcadorAsync(ArnesPilotoOutbound arnes) =>
        arnes.ComoBootstrapAsync(b =>
        {
            var nombres = CatalogoPilotoOutbound.NombresTenants.ToList();
            return b.Tenants.Where(t => nombres.Contains(t.Nombre) && t.DatosDemoCompletadosEnUtc == null).Select(t => t.Nombre).ToListAsync();
        });

    /// <summary>El almacén real, que al llegar al guardado n.º <paramref name="falloEn"/> cancela (si se le da con qué) o falla.</summary>
    private sealed class AlmacenQueSeCorta(IFileStorageService real, int falloEn, Action? cancelar = null) : IFileStorageService
    {
        private int _guardados;

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
        {
            if (++_guardados == falloEn)
            {
                if (cancelar is null)
                    throw new IOException("disco lleno de mentira");

                cancelar();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return real.GuardarAsync(contenido, nombreArchivoOriginal, cancellationToken);
        }

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            real.AbrirAsync(identificador, cancellationToken);

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            real.EliminarAsync(identificador, cancellationToken);
    }
}
