using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
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

/// <summary>Una siembra del piloto, una vez, con la variante por defecto de T4 y el correo de contactos configurado.</summary>
public sealed class PilotoOutboundFixture : IAsyncLifetime
{
    internal ArnesPilotoOutbound Arnes { get; private set; } = null!;
    internal IConfiguration Configuracion { get; private set; } = null!;
    internal PilotoOutboundSeeder.Resultado Resultado { get; private set; } = null!;
    internal Informe Informe { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Arnes = await ArnesPilotoOutbound.CrearAsync();
        Configuracion = ArnesPilotoOutbound.Configurar(
            ArnesPilotoOutbound.FechaDemostracion(),
            extra: (OpcionesPilotoOutbound.ClaveCorreoContactos, ArnesPilotoOutbound.CorreoDePrueba));

        Resultado = (await Arnes.SembrarAsync(Configuracion))!;
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
    private static readonly TenantPilotoOutbound T2 = CatalogoPilotoOutbound.T2;
    private static readonly TenantPilotoOutbound T3 = CatalogoPilotoOutbound.T3;
    private static readonly TenantPilotoOutbound T4 = CatalogoPilotoOutbound.T4;

    [Fact]
    public void La_siembra_escribio_y_la_autoverificacion_pasa()
    {
        fixture.Resultado.Escribio.Should().BeTrue();
        fixture.Resultado.TenantsConDatosNuevos.Should().Equal(
            CatalogoPilotoOutbound.EnOrdenDeSiembra.Select(t => t.Nombre), "MEDIDO: los pequeños primero y T1 el último");
        fixture.Resultado.Pdf.Should().Be(fixture.Resultado.Documentos, "MEDIDO: un PDF por documento");

        foreach (var m in fixture.Informe.Tenants)
            salida.WriteLine("MEDIDO " + PilotoOutboundTextos.Linea(m));

        PilotoOutboundAutoverificacion.Discrepancias(fixture.Informe).Should().BeEmpty();
        fixture.Informe.Tenants.Should().HaveCount(6);
    }

    [Fact]
    public void T2_esta_todo_al_dia_en_las_seis_pantallas()
    {
        var m = fixture.Informe.De(T2);

        m.MiTrabajoPresente.Should().BeTrue();
        m.MiTrabajoAlcanceCero.Should().BeFalse("MEDIDO: cero filas con alcance, no por falta de cartera");
        (m.MiTrabajoBloqueos, m.MiTrabajoActuaciones, m.MiTrabajoProximos, m.MiTrabajoSeguimiento).Should().Be((0, 0, 0, 0));
        m.InicioCumplimiento.Should().Be(100);
        (m.InicioVencidos, m.InicioUrgentes, m.InicioProximos, m.InicioSinConfirmar).Should().Be((0, 0, 0, 0));
        (m.InicioCentrosBloqueados, m.InicioTrabajadoresBloqueados, m.InicioVisitasUrgentes).Should().Be((0, 0, 0));
        m.VisionCarteraCumplimiento.Should().Be(100);
        m.EmpresaCumplimiento.Should().Be(100);
        m.Centros.Should().HaveCount(5).And.OnlyContain(c => c.Estado == EstadoCentro.Vigente && c.Cumplimiento == 100);
        (m.ParesExigidos, m.ParesFaltantes).Should().Be((65, 0), "MEDIDO: trece Asignaciones activas × cinco tipos");
        (m.ClientesEmpresariales, m.ClientesEmpresarialesSinContacto, m.ClientesEmpresarialesConAlertas).Should().Be((3, 0, 0));
        (m.Documentos, m.DocumentosSinPdf).Should().Be((77, 0), "MEDIDO: 12 Trabajadores × 5, 13 de la Empresa y 4 del Vehículo");
    }

    [Fact]
    public void T3_da_la_misma_mitad_en_Centros_Empresa_Inicio_y_Vision_de_cartera()
    {
        var m = fixture.Informe.De(T3);

        m.Centros.Should().HaveCount(4).And.OnlyContain(c => c.Cumplimiento == 50);
        m.EmpresaCumplimiento.Should().Be(50);
        m.InicioCumplimiento.Should().Be(50);
        m.VisionCarteraCumplimiento.Should().Be(50);
        (m.VisionCarteraPresente, m.VisionCarteraSinCartera, m.VisionCarteraSinDatos).Should().Be(
            (true, false, false), "MEDIDO: la fila pinta el porcentaje, no «Sin cartera» ni «Sin datos»");

        m.Centros.Select(c => c.Estado).Should().BeEquivalentTo(
            [EstadoCentro.Vencido, EstadoCentro.Faltante, EstadoCentro.Vencido, EstadoCentro.Faltante]);
        (m.ParesExigidos, m.ParesFaltantes).Should().Be((40, 10));
        (m.Documentos, m.InicioVencidos).Should().Be((20, 10));
        m.MiTrabajoFilas.Should().Be(20, "MEDIDO: por Trabajador, un documento vencido y uno que falta");
        m.MiTrabajoAlcanceCero.Should().BeFalse();
    }

    [Fact]
    public void T4_sin_documentos_da_cero_en_Centros_y_Empresa_y_declara_la_divergencia_de_Inicio_y_Vision_de_cartera()
    {
        var m = fixture.Informe.De(T4);

        m.Centros.Should().HaveCount(3).And.OnlyContain(c => c.Estado == EstadoCentro.Faltante && c.Cumplimiento == 0);
        m.EmpresaCumplimiento.Should().Be(0);
        (m.ParesExigidos, m.ParesFaltantes, m.Documentos).Should().Be((19, 19, 0));
        m.MiTrabajoFilas.Should().Be(19);

        // La divergencia declarada: sin documentos, estas dos pantallas no dicen 0 %.
        m.InicioCumplimiento.Should().Be(100);
        m.VisionCarteraCumplimiento.Should().Be(100);
        PilotoOutboundAutoverificacion.Advertencias(fixture.Informe).Should().ContainSingle()
            .Which.Should().StartWith($"T4 «{CatalogoPilotoOutbound.NombreTenantT4}»").And.Contain("Inicio 100 %").And.Contain("Visión de cartera 100 %");
    }

    [Fact]
    public void Los_Tenants_en_esqueleto_existen_tienen_agenda_y_estan_en_la_cartera_de_la_Gestora_CAE()
    {
        foreach (var tenant in new[] { CatalogoPilotoOutbound.T1, CatalogoPilotoOutbound.T5, CatalogoPilotoOutbound.T6 })
        {
            var m = fixture.Informe.De(tenant);
            m.MiTrabajoPresente.Should().BeTrue($"MEDIDO: {tenant.Clave} está en la cartera de la Gestora CAE primera");
            m.ContactosDeAgenda.Should().BeGreaterThan(0);
            (m.Documentos, m.Centros.Count).Should().Be((0, 0));
        }
    }

    [Fact]
    public async Task Los_resultados_no_cambian_entre_el_dia_del_ensayo_y_el_de_la_demostracion()
    {
        var fecha = ArnesPilotoOutbound.FechaDemostracion();
        DiaDeNegocio.Hoy().Should().Be(fecha.AddDays(-5), "control: sin fijar el reloj, hoy es D−5");

        Informe enLaDemostracion;
        using (DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojEn(fecha.ToDateTime(new TimeOnly(11, 0), DateTimeKind.Utc))))
        {
            DiaDeNegocio.Hoy().Should().Be(fecha, "control: el reloj fijado mueve el día de negocio");
            enLaDemostracion = await fixture.Arnes.MedirAsync(fixture.Configuracion);
        }

        PilotoOutboundAutoverificacion.Discrepancias(enLaDemostracion).Should().BeEmpty();
        enLaDemostracion.Tenants.Should().BeEquivalentTo(fixture.Informe.Tenants, "MEDIDO: lo que se ensaya el día D−5 es lo que se ve el día D");
    }

    [Fact]
    public async Task Cada_documento_se_abre_como_PDF_y_exactamente_uno_pesa_entre_5_MB_y_10_MiB()
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

        tamanos.Should().HaveCount(97, "MEDIDO: 77 de T2 y 20 de T3");
        var pesados = tamanos.Where(t => t.Bytes >= 5_000_000).ToList();
        salida.WriteLine($"MEDIDO PDF pesado: {string.Join(", ", pesados.Select(p => $"{p.Tenant} {p.Bytes} bytes"))}; el mayor de los demás: {tamanos.Where(t => t.Bytes < 5_000_000).Max(t => t.Bytes)} bytes");
        pesados.Should().ContainSingle().Which.Should().Match<(string Tenant, long Bytes)>(p => p.Tenant == "T2" && p.Bytes <= 10 * 1024 * 1024);
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

    [Fact]
    public async Task El_Gestor_CAE_segundo_ve_en_Mi_trabajo_solo_T2_T3_y_T6_y_la_Gestora_CAE_primera_los_seis()
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

        (await ClavesVistasPorAsync(CuentasPilotoOutbound.Locales.GestorSegundo)).Should().Equal("T2", "T3", "T6");
        (await ClavesVistasPorAsync(CuentasPilotoOutbound.Locales.GestoraPrimera)).Should().Equal("T1", "T2", "T3", "T4", "T5", "T6");
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
            fixture.Arnes.FabricaDeAmbitos, cuentas, ContactosPilotoOutbound.NoEntregables,
            VarianteT4PilotoOutbound.SinDocumentos, CancellationToken.None);

        await medicion.Should().ThrowAsync<InvalidOperationException>().WithMessage(mensaje);
        fixture.Arnes.Servicios.GetRequiredService<IHttpContextAccessor>().HttpContext.Should().BeNull();
    }

    [Fact]
    public async Task Una_segunda_siembra_no_escribe_nada()
    {
        var antes = await fixture.Arnes.RecuentoAsync();
        antes["Tenants del piloto"].Should().Be(7, "control: el recuento ve los seis Tenants propietarios y el del Operador CAE externo");
        antes["Cuentas del piloto"].Should().Be(5);
        antes["Documentos"].Should().Be(97);
        antes["Ficheros en el almacén"].Should().Be(97);

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
        $"{m.Clave}: Mi trabajo presente={m.MiTrabajoPresente} alcanceCero={m.MiTrabajoAlcanceCero} noConsultado={m.MiTrabajoNoConsultado} " +
        $"filas={m.MiTrabajoFilas} (bloqueos {m.MiTrabajoBloqueos}, actuaciones {m.MiTrabajoActuaciones}, próximos {m.MiTrabajoProximos}, " +
        $"seguimiento {m.MiTrabajoSeguimiento}) | Inicio {m.InicioCumplimiento}% vencidos={m.InicioVencidos} urgentes={m.InicioUrgentes} " +
        $"próximos={m.InicioProximos} sinConfirmar={m.InicioSinConfirmar} centrosBloqueados={m.InicioCentrosBloqueados} " +
        $"trabajadoresBloqueados={m.InicioTrabajadoresBloqueados} visitasUrgentes={m.InicioVisitasUrgentes} | Visión de cartera " +
        $"presente={m.VisionCarteraPresente} sinCartera={m.VisionCarteraSinCartera} sinDatos={m.VisionCarteraSinDatos} {m.VisionCarteraCumplimiento}% | Empresa {m.EmpresaCumplimiento}% | Centros " +
        $"[{string.Join("; ", m.Centros.Select(c => $"{c.Estado} {c.Cumplimiento}%"))}] | pares {m.ParesExigidos} (faltantes {m.ParesFaltantes}) | " +
        $"Clientes empresariales {m.ClientesEmpresariales} (sin contacto {m.ClientesEmpresarialesSinContacto}, con alertas " +
        $"{m.ClientesEmpresarialesConAlertas}) | documentos {m.Documentos} (sin PDF {m.DocumentosSinPdf}) | agenda {m.ContactosDeAgenda} " +
        $"(fuera de regla {m.ContactosFueraDeLaReglaDeCorreo})";
}

/// <summary>
/// Sensibilidad de la autoverificación sobre datos que dejan de ser los de la
/// matriz, y que medir no escribe: su propia base, porque la estropea.
/// </summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class PilotoOutboundSensibilidadTests(ITestOutputHelper salida)
{
    [Fact]
    public async Task Medir_no_escribe_y_Exigir_nombra_el_Tenant_y_el_contador_cuando_los_datos_se_apartan_de_la_matriz()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var configuracion = ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion());
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

        // 2. Un documento de Trabajador de T2 pasa a estar vencido.
        var t2 = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantT2);
        await arnes.EnTenantAsync(t2, async (db, _) =>
        {
            var documento = await db.Documentos.Where(d => d.TrabajadorId != null && d.FechaVencimiento != null).OrderBy(d => d.Id).FirstAsync();
            var vence = DiaDeNegocio.Hoy().AddDays(-20);
            documento.CorregirVigencia(vence.AddYears(-1), VigenciaDocumento.VenceEl(vence));
            return await db.SaveChangesAsync();
        });

        (await HuellaDeEscriturasAsync(arnes)).Should().NotBe(huellaAntes, "control positivo: la huella sí ve una escritura de verdad");

        var conVencido = PilotoOutboundAutoverificacion.Discrepancias(await arnes.MedirAsync(configuracion));
        foreach (var linea in conVencido) salida.WriteLine("MEDIDO " + linea);

        var prefijoT2 = $"T2 «{CatalogoPilotoOutbound.NombreTenantT2}» · ";
        conVencido.Should().NotBeEmpty().And.OnlyContain(l => l.StartsWith(prefijoT2), "MEDIDO: solo se estropeó T2");
        conVencido.Should().Contain(prefijoT2 + "Inicio · documentos vencidos: medido 1, esperado 0.");
        conVencido.Should().Contain(l => l.StartsWith(prefijoT2 + "Mi trabajo · filas: medido "));
        conVencido.Should().Contain(l => l.StartsWith(prefijoT2 + "Empresas · % de cumplimiento de la Empresa propia: medido "));

        // 3. Un documento vigente de T3 desaparece.
        var t3 = await arnes.TenantIdAsync(CatalogoPilotoOutbound.NombreTenantT3);
        await arnes.EnTenantAsync(t3, async (db, _) =>
        {
            var hoy = DiaDeNegocio.Hoy();
            db.Documentos.Remove(await db.Documentos.Where(d => d.FechaVencimiento > hoy).OrderBy(d => d.Id).FirstAsync());
            return await db.SaveChangesAsync();
        });

        var exigir = async () => PilotoOutboundAutoverificacion.Exigir(await arnes.MedirAsync(configuracion));
        var mensaje = (await exigir.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        salida.WriteLine("MEDIDO " + mensaje);

        var prefijoT3 = $"T3 «{CatalogoPilotoOutbound.NombreTenantT3}» · ";
        mensaje.Should().Contain(prefijoT3 + "Documentos · total: medido 19, esperado 20.");
        mensaje.Should().Contain(prefijoT3 + "Inicio · % de cumplimiento: medido ");
        mensaje.Should().Contain(prefijoT3 + "Visión de cartera · % de cumplimiento: medido ");
        mensaje.Should().NotContain($"T4 «{CatalogoPilotoOutbound.NombreTenantT4}»");
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

/// <summary>La variante de T4 con un documento vencido por cada par exigido, y la agenda sin correo configurado.</summary>
[Collection(ColeccionPilotoOutbound.Nombre)]
public class PilotoOutboundVarianteDocumentosVencidosTests(ITestOutputHelper salida)
{
    [Fact]
    public async Task T4_con_documentos_vencidos_da_cero_en_las_cuatro_pantallas_y_la_agenda_no_es_entregable()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var configuracion = ArnesPilotoOutbound.Configurar(
            ArnesPilotoOutbound.FechaDemostracion(),
            extra:
            [
                (OpcionesPilotoOutbound.ClaveVarianteT4, nameof(VarianteT4PilotoOutbound.DocumentosVencidos)),
                (OpcionesPilotoOutbound.ClaveDominioContactos, "contactos.example")
            ]);

        await arnes.SembrarAsync(configuracion);
        await arnes.BackfillAsync();
        var informe = await arnes.MedirAsync(configuracion);

        foreach (var medicion in informe.Tenants)
            salida.WriteLine("MEDIDO " + PilotoOutboundTextos.Linea(medicion));

        PilotoOutboundAutoverificacion.Discrepancias(informe).Should().BeEmpty();
        PilotoOutboundAutoverificacion.Advertencias(informe).Should().BeEmpty("MEDIDO: esta variante no tiene divergencia que declarar");

        var m = informe.De(CatalogoPilotoOutbound.T4);
        m.Centros.Should().HaveCount(3).And.OnlyContain(c => c.Estado == EstadoCentro.Vencido && c.Cumplimiento == 0);
        m.EmpresaCumplimiento.Should().Be(0);
        m.InicioCumplimiento.Should().Be(0);
        m.VisionCarteraCumplimiento.Should().Be(0);
        (m.ParesExigidos, m.ParesFaltantes, m.Documentos, m.DocumentosSinPdf, m.InicioVencidos).Should().Be((19, 0, 19, 0, 19));
        m.MiTrabajoFilas.Should().Be(19);

        // La otra variante no cambia a los demás.
        informe.De(CatalogoPilotoOutbound.T2).InicioCumplimiento.Should().Be(100);
        informe.De(CatalogoPilotoOutbound.T3).InicioCumplimiento.Should().Be(50);

        foreach (var tenant in CatalogoPilotoOutbound.Tenants)
        {
            var tenantId = await arnes.TenantIdAsync(tenant.Nombre);
            var correos = await arnes.EnTenantAsync(tenantId, (db, _) => db.ContactosAgenda.Select(c => c.Email).ToListAsync());
            correos.Should().NotBeEmpty().And.OnlyContain(c => c.EndsWith("@contactos.example") && !c.Contains('+'));
        }
    }
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
    [InlineData("VarianteT4", "Vencidos", "*no es una variante conocida*SinDocumentos, DocumentosVencidos*")]
    [InlineData("VarianteT4", "1", "*no es una variante conocida*")]
    [InlineData("CorreoContactos", "ensayo+yo@destino.example", "*CorreoContactos no es una dirección utilizable*")]
    [InlineData("CorreoContactos", "sin-arroba", "*CorreoContactos no es una dirección utilizable*")]
    public async Task Con_una_variante_o_un_correo_de_contactos_que_no_valen_se_niega_antes_de_escribir(string clave, string valor, string mensaje)
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

    [Fact]
    public void La_regla_de_correo_de_los_contactos_distingue_el_buzon_configurado_del_dominio_no_entregable()
    {
        var conCorreo = ContactosPilotoOutbound.Crear(ArnesPilotoOutbound.CorreoDePrueba, null);
        conCorreo.DireccionDe("t2-centro1").Should().Be("ensayo+t2-centro1@destino.example");
        conCorreo.Cumple("ensayo+t2-centro1@destino.example").Should().BeTrue();
        conCorreo.Cumple("ensayo@destino.example").Should().BeFalse("sin etiqueta no se sabe a qué contacto se escribió");
        conCorreo.Cumple("otra+t2-centro1@destino.example").Should().BeFalse();
        conCorreo.Cumple("t2-centro1@caemanager.local").Should().BeFalse();

        var sinCorreo = ContactosPilotoOutbound.Crear(" ", null);
        sinCorreo.Should().Be(ContactosPilotoOutbound.NoEntregables);
        sinCorreo.DireccionDe("t2-centro1").Should().Be("t2-centro1@caemanager.local");
        sinCorreo.Cumple("ensayo+t2-centro1@destino.example").Should().BeFalse();
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
    // T4 no escribe ningún PDF y T3 escribe 20: el n.º 30 es el décimo de T2.
    private const int PdfEnElQueFalla = 30;
    private const int PdfDeT3 = 20;

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

        // 2. Reanudar sin retirar: se completa lo que faltaba y la matriz sale.
        var reanudada = await arnes.SembrarAsync(configuracion);
        reanudada!.TenantsConDatosNuevos.Should().Equal(
            CatalogoPilotoOutbound.NombreTenantT2, CatalogoPilotoOutbound.NombreTenantT5,
            CatalogoPilotoOutbound.NombreTenantT6, CatalogoPilotoOutbound.NombreTenantT1);
        await arnes.BackfillAsync();
        PilotoOutboundAutoverificacion.Exigir(await arnes.MedirAsync(configuracion));
        var completa = await arnes.RecuentoAsync();
        completa["Ficheros en el almacén"].Should().Be(97);

        // 3. La retirada no deja nada del piloto y no toca lo demás.
        (await arnes.RetirarAsync()).Should().HaveCount(7);
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(sinPiloto, "MEDIDO: ni Tenants, ni cuentas, ni filas, ni ficheros del piloto; lo demás, igual que antes");
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
                CatalogoPilotoOutbound.NombreTenantT3, PerfilVocabularioTenant.ClienteDirecto, NullLogger.Instance,
                CancellationToken.None, esOperadorCaeExterno: false);

        var antes = await arnes.RecuentoAsync();
        antes["Tenants del piloto"].Should().Be(1, "control: el Tenant que ocupa el nombre existe");

        var siembra = () => arnes.SembrarAsync(ArnesPilotoOutbound.Configurar(ArnesPilotoOutbound.FechaDemostracion()));

        await siembra.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*«{CatalogoPilotoOutbound.NombreTenantT3}»*NO lleva el marcador de datos de demo*");
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(antes, "MEDIDO: no escribe ni en ese Tenant ni a su lado");

        var retirada = () => arnes.RetirarAsync();
        await retirada.Should().ThrowAsync<InvalidOperationException>("la retirada tampoco toca un Tenant sin marcador");
        (await arnes.RecuentoAsync()).Should().BeEquivalentTo(antes);
    }

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
