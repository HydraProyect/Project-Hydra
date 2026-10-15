using System.IO.Compression;
using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesSinRespuesta;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Application.Visitas.Queries.ObtenerPaqueteDocumentalVisita;
using CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;
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
        fixture.Resultado.Documentos.Should().BeGreaterThan(500, "control: los siete perfiles siembran documentos");

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
            "MEDIDO Centros «Vigente» en los siete Tenants: " +
            fixture.Informe.Tenants.Sum(m => m.Centros.Count(c => c.Estado == EstadoCentro.Vigente)));

        PilotoOutboundAutoverificacion.Discrepancias(fixture.Informe).Should().BeEmpty();
        fixture.Informe.Tenants.Should().HaveCount(7);
    }

    /// <summary>
    /// Lo que el Asistente IA comprueba antes de enviar nada al proveedor, con la identidad de las dos cuentas que
    /// lo usan en la demostración: el Tenant en pantalla y toda la cartera tienen instrucción de tratamiento de IA
    /// vigente. Se afirma por nombre contra los siete del catálogo: una cartera vacía también «pasaría».
    /// </summary>
    [Fact]
    public async Task El_asistente_IA_no_falla_cerrado_para_la_Gestora_CAE_ni_para_la_Coordinadora_CAE()
    {
        var asistenteIa = fixture.Informe.AsistenteIa;
        asistenteIa.Should().NotBeNull("control: la autoverificación midió la comprobación de cartera");
        var vigentes = CatalogoPilotoOutbound.Tenants.Select(t => t.Nombre).Append(CatalogoPilotoOutbound.NombreTenantOperador).ToList();
        vigentes.Should().HaveCount(8, "control: el Operador CAE externo y los siete Tenants propietarios");

        foreach (var (cuenta, cartera) in new[] { ("Gestora CAE", asistenteIa!.Gestora), ("Coordinadora CAE", asistenteIa.Coordinadora) })
        {
            salida.WriteLine($"MEDIDO cartera de la {cuenta}: con instrucción {cartera.ConInstruccion.Count}, sin instrucción {cartera.SinInstruccion.Count}");
            cartera.SinInstruccion.Should().BeEmpty($"MEDIDO: ningún Tenant de la cartera de la {cuenta} está sin instrucción");
            cartera.ConInstruccion.Should().BeEquivalentTo(
                vigentes, $"MEDIDO: la cartera de la {cuenta} son los ocho Tenants del piloto, todos con instrucción");
        }

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

    [Fact]
    public async Task Ningun_nombre_completo_de_Trabajador_se_repite_en_los_siete_Tenants()
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
            .Should().BeEmpty("MEDIDO: ningún nombre completo se repite entre los Trabajadores de los seis Tenants");
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
    /// Subcontrata, Centros, Vehículos, Proyectos y Subcontratas de cada uno de los siete perfiles, y la invariante de que
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
            ("G", 8, 0, 1, 0, 0, 0),
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
    public async Task Las_fechas_de_cada_documento_son_las_que_el_producto_le_habria_dado_y_solo_cinco_tipos_quedan_sin_caducidad()
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
        conVencimientoAutomatico.Should().BeGreaterThan(100, "control: se miraron los documentos de los siete Tenants, no un conjunto vacío");
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
        antes["Tenants del piloto"].Should().Be(8, "control: el recuento ve los siete Tenants propietarios y el del Operador CAE externo");
        antes["Cuentas del piloto"].Should().Be(6, "MEDIDO: las cinco del equipo y de T1, y el Usuario de Cliente de T1");
        antes["Cuenta pendiente de activación"].Should().Be(1, "MEDIDO: la séptima, fuera de las seis que tienen contraseña");
        (antes["Solicitudes de incorporación a cartera"], antes["Propuestas de apoyo"]).Should().Be((0, 0), "MEDIDO: no se siembran: sus políticas exigen el Tenant de origen de una sesión autenticada");
        antes["Ficheros en el almacén"].Should().Be(antes["Documentos"]);

        var segunda = await fixture.Arnes.SembrarAsync(fixture.Configuracion);

        segunda!.Escribio.Should().BeFalse();
        segunda.TenantsConDatosNuevos.Should().BeEmpty();
        (await fixture.Arnes.RecuentoAsync()).Should().BeEquivalentTo(antes, "MEDIDO: ni una fila, ni una cuenta, ni un fichero más");
        salida.WriteLine("MEDIDO recuento: " + string.Join(", ", antes.Select(p => $"{p.Key} {p.Value}")));
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

        // 2. Un documento de Trabajador del perfil A pasa a estar vencido: solo A se aparta del diseño.
        var a = CatalogoPilotoOutbound.A;
        var idA = await arnes.TenantIdAsync(a.Nombre);
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
    // T4 no escribe ningún PDF y T3 escribe 33 (20 de Trabajador y 13 de su Empresa propia): el n.º 43 es el décimo de T2.
    private const int PdfEnElQueFalla = 43;
    private const int PdfDeT3 = 33;

    [Fact]
    public async Task Una_siembra_cortada_limpia_los_PDF_de_su_Tenant_se_reanuda_se_retira_entera_y_se_vuelve_a_sembrar()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var configuracion = ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion());
        var sinPiloto = await arnes.RecuentoAsync();

        // 1. El almacén falla en mitad de T2.
        var conFallo = () => arnes.SembrarAsync(configuracion, envolverAlmacen: real => new AlmacenQueSeCorta(real, PdfEnElQueFalla));
        await conFallo.Should().ThrowAsync<IOException>().WithMessage("*disco lleno de mentira*");

        var trasElFallo = await arnes.RecuentoAsync();
        salida.WriteLine("MEDIDO tras el fallo del almacén: " + Texto(trasElFallo));
        trasElFallo["Ficheros en el almacén"].Should().Be(PdfDeT3, "MEDIDO: los nueve PDF que T2 ya había escrito se eliminan; quedan los de T3, que sí se guardó");
        trasElFallo["Documentos"].Should().Be(PdfDeT3);
        (await TenantsSinMarcadorAsync(arnes)).Should().BeEmpty("MEDIDO: todo Tenant del piloto lleva el marcador desde que nace");

        // 1 bis. Con la fecha de la demostración ya pasada y T2 a medias, la siembra se niega: queda algo por escribir.
        var conLaFechaPasada = () => arnes.SembrarAsync(ArnesPilotoOutbound.Configurar(DiaDeNegocio.Hoy().AddDays(-1)));
        await conLaFechaPasada.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*es anterior a hoy*Queda por sembrar «{CatalogoPilotoOutbound.NombreTenantA}»*");
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(trasElFallo, "MEDIDO: el rechazo no escribe");

        // 2. Reanudar sin retirar: se completa lo que faltaba y la matriz sale.
        var reanudada = await arnes.SembrarAsync(configuracion);
        reanudada!.TenantsConDatosNuevos.Should().Equal(
            CatalogoPilotoOutbound.NombreTenantA, CatalogoPilotoOutbound.NombreTenantF,
            CatalogoPilotoOutbound.NombreTenantD, CatalogoPilotoOutbound.NombreTenantC);
        await arnes.BackfillAsync();
        PilotoOutboundAutoverificacion.Exigir(await arnes.MedirAsync(configuracion));
        var completa = await arnes.RecuentoAsync();
        completa["Ficheros en el almacén"].Should().Be(1165);
        var escenariosDelOperador = new[] { "Cuenta pendiente de activación",
            "Revisiones de IA", "Detecciones de Trabajador", "Sugerencias de Visita" };
        escenariosDelOperador.Select(k => completa[k]).Should().Equal([1, 1, 1, 1], "control positivo: la siembra completa los tiene");

        // 3. La retirada no deja nada del piloto y no toca lo demás.
        (await arnes.RetirarAsync()).Should().HaveCount(7);
        var retirada = await arnes.RecuentoAsync();
        retirada.Should().BeEquivalentTo(sinPiloto, "MEDIDO: ni Tenants, ni cuentas, ni filas, ni ficheros del piloto; lo demás, igual que antes");
        escenariosDelOperador.Select(k => retirada[k]).Should().Equal([0, 0, 0, 0], "MEDIDO: la retirada borra la cuenta pendiente y las tres señales de IA y correo");
        sinPiloto["Tenants en total"].Should().BeGreaterThan(0, "control positivo: había otros Tenants que no debían tocarse");

        // 4. Cancelación en mitad de T2.
        using var cancelacion = new CancellationTokenSource();
        var cancelada = () => arnes.SembrarAsync(
            configuracion, envolverAlmacen: real => new AlmacenQueSeCorta(real, PdfEnElQueFalla, cancelacion.Cancel), cancellationToken: cancelacion.Token);
        await cancelada.Should().ThrowAsync<OperationCanceledException>();

        var trasCancelar = await arnes.RecuentoAsync();
        salida.WriteLine("MEDIDO tras la cancelación: " + Texto(trasCancelar));
        trasCancelar["Ficheros en el almacén"].Should().Be(PdfDeT3, "MEDIDO: la limpieza corre aunque la causa sea la cancelación");

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
    /// siete Tenants con todos sus datos y ninguno con instrucción. El asistente falla cerrado —es el estado que
    /// este paso corrige—, la resiembra añade las siete instrucciones sin tocar nada más, repetirla no añade
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
            CatalogoPilotoOutbound.NombresTenants, "MEDIDO: exactamente una por Tenant del piloto, el Operador CAE externo y los seis propietarios");
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
        })).Should().Be(7, "control: se han quitado las siete");
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
            CatalogoPilotoOutbound.NombresTenants, "MEDIDO: los siete Tenants de la cartera de la Gestora CAE están sin instrucción");
        carteraSinInstruccion.ErrorSiFalta()!.Codigo.Should().Be(InstruccionIaCarteraDto.CodigoError);

        var discrepancias = PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion));
        foreach (var linea in discrepancias) salida.WriteLine("MEDIDO " + linea);
        discrepancias.Should().HaveCount(14, "MEDIDO: una por Tenant y por cuenta, siete de la Gestora CAE y siete de la Coordinadora CAE");
        discrepancias.Should().OnlyContain(l => l.StartsWith("Asistente IA · "), "control: del lote solo falla el Asistente IA");
        discrepancias.Should().Contain(
            $"Asistente IA · cartera de la Coordinadora CAE · «{CatalogoPilotoOutbound.NombreTenantC}»: medido sin instrucción de tratamiento de IA " +
            "vigente, esperado con instrucción de tratamiento de IA vigente.");

        // 4. La resiembra: no hay nada que sembrar salvo la instrucción.
        var resiembra = await arnes.SembrarAsync(configuracion);
        resiembra!.Escribio.Should().BeFalse("MEDIDO: añadir la instrucción que faltaba no cuenta como haber escrito el lote");
        resiembra.TenantsConDatosNuevos.Should().BeEmpty();
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(completa, "MEDIDO: vuelve a haber siete instrucciones y nada más ha cambiado");
        (await InstruccionesDelPilotoAsync(arnes)).Select(i => i.Tenant).Should().BeEquivalentTo(
            CatalogoPilotoOutbound.NombresTenants, "MEDIDO: una por Tenant, no siete en uno");
        PilotoOutboundAutoverificacion.Exigir(await arnes.MedirAsync(configuracion));

        var carteraConInstruccion = await arnes.ComoCuentaAsync(
            CatalogoPilotoOutbound.NombreTenantOperador, CuentasPilotoOutbound.Locales.Coordinadora,
            sp => sp.GetRequiredService<ISender>().Send(new ComprobarInstruccionIaCarteraQuery()));
        carteraConInstruccion.ErrorSiFalta().Should().BeNull("MEDIDO: la comprobación de cartera pasa para la Coordinadora CAE");
        carteraConInstruccion.ConInstruccion.Should().HaveCount(7, "control: y pasa con los siete en la cartera, no con una cartera vacía");

        // 5. Repetirla no añade ninguna.
        await arnes.SembrarAsync(configuracion);
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(completa, "MEDIDO: idempotente, siguen siendo siete");

        // 6. Una instrucción revocada a propósito en un ensayo no se repone: el Tenant ya tiene la suya, cerrada.
        var t2Id = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantA);
        (await arnes.EnTenantAsync(t2Id, async (db, _) =>
        {
            (await db.InstruccionesTratamientoIaTenantPropietario.SingleAsync()).Revocar("Ensayo del fallo cerrado.", DateTime.UtcNow);
            return await db.SaveChangesAsync();
        })).Should().BeGreaterThan(0, "control: la revocación se guardó");
        await arnes.SembrarAsync(configuracion);
        var trasRevocar = await InstruccionesDelPilotoAsync(arnes);
        trasRevocar.Should().HaveCount(7, "MEDIDO: la siembra no añade otra al Tenant cuya instrucción se revocó");
        trasRevocar.Where(i => !i.Vigente).Select(i => i.Tenant).Should().Equal(
            [CatalogoPilotoOutbound.NombreTenantA], "MEDIDO: y la revocada sigue revocada");
        PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion)).Should().HaveCount(2, "MEDIDO: la autoverificación lo dice, una vez por cuenta")
            .And.OnlyContain(l => l.StartsWith("Asistente IA · ") && l.Contains($"«{CatalogoPilotoOutbound.NombreTenantA}»: medido sin instrucción"));

        // 7. La retirada se las lleva con el resto del lote, también la revocada.
        completa[Clave].Should().Be(7, "control positivo: antes de retirar hay siete que borrar");
        (await arnes.RetirarAsync()).Should().HaveCount(7);
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
