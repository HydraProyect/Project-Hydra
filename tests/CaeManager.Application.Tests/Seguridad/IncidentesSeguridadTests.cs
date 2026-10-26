using System.Text.Json;
using CaeManager.Application.Seguridad.Incidentes;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Application.Tests.Seguridad;

public class VentanaDeslizantePorSujetoTests
{
    private static readonly DateTime Ahora = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Hora = TimeSpan.FromHours(1);

    [Fact]
    public void Salta_al_alcanzar_el_umbral_y_solo_una_vez_por_ventana()
    {
        var ventana = new VentanaDeslizantePorSujeto();

        ventana.Sumar("u", 3_000, 5_000, Hora, Ahora).Should().BeFalse();
        ventana.Sumar("u", 2_000, 5_000, Hora, Ahora.AddMinutes(10)).Should().BeTrue("3.000 + 2.000 alcanza el umbral");
        ventana.Sumar("u", 2_000, 5_000, Hora, Ahora.AddMinutes(20)).Should().BeFalse("ya avisó en esta ventana");
        ventana.SumaActual("u").Should().Be(7_000);
    }

    [Fact]
    public void Lo_que_sale_de_la_ventana_deja_de_contar()
    {
        var ventana = new VentanaDeslizantePorSujeto();

        ventana.Sumar("u", 4_000, 5_000, Hora, Ahora).Should().BeFalse();
        ventana.Sumar("u", 4_000, 5_000, Hora, Ahora.AddMinutes(61)).Should().BeFalse("las primeras 4.000 ya salieron de la ventana");
        ventana.SumaActual("u").Should().Be(4_000);
    }

    [Fact]
    public void Tras_pasar_la_ventana_avisada_puede_volver_a_saltar()
    {
        var ventana = new VentanaDeslizantePorSujeto();

        ventana.Sumar("u", 5_000, 5_000, Hora, Ahora).Should().BeTrue();
        ventana.Sumar("u", 5_000, 5_000, Hora, Ahora.AddMinutes(61)).Should().BeTrue();
    }

    [Fact]
    public void Los_sujetos_no_se_mezclan_y_las_etiquetas_se_devuelven_distintas_y_ordenadas()
    {
        var ventana = new VentanaDeslizantePorSujeto();
        var a = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var b = Guid.Parse("00000000-0000-0000-0000-00000000000b");

        ventana.Sumar("u1", 1, 3, Hora, Ahora, b);
        ventana.Sumar("u1", 1, 3, Hora, Ahora, a);
        ventana.Sumar("u1", 1, 3, Hora, Ahora, b);
        ventana.Sumar("u2", 1, 3, Hora, Ahora, a).Should().BeFalse("u2 lleva uno, no tres");

        ventana.EtiquetasEnVentana("u1").Should().Equal(a, b);
        ventana.EtiquetasEnVentana("nadie").Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    public void Peso_o_umbral_no_positivos_no_cuentan(long peso, long umbral)
        => new VentanaDeslizantePorSujeto().Sumar("u", peso, umbral, Hora, Ahora).Should().BeFalse();
}

public class EvidenciaIncidenteSeguridadTests
{
    [Fact]
    public void Serializa_solo_identificadores_recuentos_booleanos_fechas_y_enumeraciones()
    {
        var usuario = Guid.NewGuid();
        var json = new EvidenciaIncidenteSeguridad()
            .Con("usuario_id", usuario)
            .Con("filas", 5_000L)
            .Con("con_contrasena", true)
            .Con("senal", SenalIncidenteSeguridad.ExtraccionMasiva)
            .Con("momento", new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc))
            .ComoJson();

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("usuario_id").GetString().Should().Be(usuario.ToString("D"));
        doc.RootElement.GetProperty("filas").GetInt64().Should().Be(5_000);
        doc.RootElement.GetProperty("con_contrasena").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("senal").GetString().Should().Be("ExtraccionMasiva");
        doc.RootElement.GetProperty("momento").GetString().Should().StartWith("2026-10-26T12:00:00");
    }

    [Fact]
    public void No_existe_ninguna_sobrecarga_que_acepte_texto_libre()
    {
        // La garantía de «sin datos personales en claro» es de tipos: si alguien añade
        // Con(string, string), un nombre o un correo podrían entrar en la evidencia.
        typeof(EvidenciaIncidenteSeguridad).GetMethods()
            .Where(m => m.Name == nameof(EvidenciaIncidenteSeguridad.Con))
            .SelectMany(m => m.GetParameters().Skip(1))
            .Select(p => p.ParameterType)
            .Should().NotContain(t => t == typeof(string) || t == typeof(object) || t == typeof(IEnumerable<string>));
    }

    [Theory]
    [InlineData("")]
    [InlineData("con espacio")]
    [InlineData("correo@dominio")]
    public void Rechaza_claves_que_no_son_identificadores_cortos(string clave)
        => FluentActions.Invoking(() => new EvidenciaIncidenteSeguridad().Con(clave, 1L)).Should().Throw<ArgumentException>();
}

public class SenalesIncidenteSeguridadTests
{
    private readonly RegistroFalso _registro = new();
    private readonly AlertaFalsa _alerta = new();
    private readonly RelojFijo _reloj = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly OpcionesIncidentesSeguridad _opciones = new();

    private SenalesIncidenteSeguridad Crear() => new(
        _registro, _alerta, Options.Create(_opciones), _reloj, NullLogger<SenalesIncidenteSeguridad>.Instance);

    [Fact]
    public async Task No_he_sido_yo_abre_un_incidente_al_momento_y_encola_la_alerta()
    {
        var usuario = Guid.NewGuid();
        var tenant = Guid.NewGuid();

        await Crear().InicioSesionNoReconocidoAsync(usuario, tenant, teniaContrasena: true);

        var llamada = _registro.Llamadas.Should().ContainSingle().Subject;
        llamada.Senal.Should().Be(SenalIncidenteSeguridad.InicioSesionNoReconocido);
        llamada.Sujeto.Should().Be($"usuario:{usuario:D}");
        llamada.Tenants.Should().Equal(tenant);
        llamada.Ventana.Should().Be(TimeSpan.FromMinutes(60));
        _alerta.Encolados.Should().Equal(_registro.UltimoId);
    }

    [Fact]
    public async Task Una_deteccion_agrupada_en_un_incidente_abierto_no_vuelve_a_avisar()
    {
        _registro.SiguienteEsNuevo = false;

        await Crear().InicioSesionNoReconocidoAsync(Guid.NewGuid(), Guid.NewGuid(), teniaContrasena: false);

        _registro.Llamadas.Should().ContainSingle();
        _alerta.Encolados.Should().BeEmpty();
    }

    [Fact]
    public async Task La_exportacion_solo_registra_al_superar_el_umbral_de_filas()
    {
        var senales = Crear();
        var usuario = Guid.NewGuid();
        var tenant = Guid.NewGuid();

        await senales.ExportacionAsync(usuario, tenant, 4_999);
        _registro.Llamadas.Should().BeEmpty();

        await senales.ExportacionAsync(usuario, tenant, 1);
        var llamada = _registro.Llamadas.Should().ContainSingle().Subject;
        llamada.Senal.Should().Be(SenalIncidenteSeguridad.ExtraccionMasiva);
        llamada.Tenants.Should().Equal(tenant);
        llamada.Evidencia.Should().Contain("\"filas_exportadas_en_ventana\":5000");
    }

    [Fact]
    public async Task Los_documentos_sensibles_cuentan_uno_a_uno_con_su_propio_umbral()
    {
        _opciones.UmbralDocumentosSensibles = 3;
        var senales = Crear();
        var usuario = Guid.NewGuid();

        await senales.DocumentoSensibleAbiertoAsync(usuario, Guid.NewGuid());
        await senales.DocumentoSensibleAbiertoAsync(usuario, Guid.NewGuid());
        _registro.Llamadas.Should().BeEmpty();

        await senales.DocumentoSensibleAbiertoAsync(usuario, Guid.NewGuid());
        _registro.Llamadas.Should().ContainSingle().Which.Tenants.Should().HaveCount(3);
    }

    [Fact]
    public async Task Los_rechazos_a_otro_Tenant_registran_al_quinto_con_el_Tenant_de_origen_como_afectado()
    {
        var senales = Crear();
        var usuario = Guid.NewGuid();
        var origen = Guid.NewGuid();
        var objetivos = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();

        foreach (var objetivo in objetivos.Take(4))
            await senales.AccesoAOtroTenantRechazadoAsync(usuario, origen, objetivo);
        _registro.Llamadas.Should().BeEmpty();

        await senales.AccesoAOtroTenantRechazadoAsync(usuario, origen, objetivos[4]);
        var llamada = _registro.Llamadas.Should().ContainSingle().Subject;
        llamada.Senal.Should().Be(SenalIncidenteSeguridad.AccesoRepetidoAOtroTenant);
        llamada.Tenants.Should().Equal([origen],
            "los Tenants pedidos los elige quien envía la petición: si fueran los afectados, cualquiera dirigiría el aviso a Tenants ajenos");
        using var evidencia = JsonDocument.Parse(llamada.Evidencia);
        evidencia.RootElement.GetProperty("tenants_objetivo_ids").EnumerateArray().Select(e => Guid.Parse(e.GetString()!))
            .Should().BeEquivalentTo(objetivos, "los Tenants pedidos solo van a la evidencia");
    }

    [Fact]
    public async Task Si_el_registro_falla_la_ventana_se_desmarca_y_la_operacion_siguiente_lo_reintenta()
    {
        _opciones.UmbralDocumentosSensibles = 2;
        var senales = Crear();
        var usuario = Guid.NewGuid();
        await senales.DocumentoSensibleAbiertoAsync(usuario, Guid.NewGuid());

        _registro.Fallar = true;
        await senales.DocumentoSensibleAbiertoAsync(usuario, Guid.NewGuid());
        _registro.Llamadas.Should().HaveCount(1);

        _registro.Fallar = false;
        await senales.DocumentoSensibleAbiertoAsync(usuario, Guid.NewGuid());
        _registro.Llamadas.Should().HaveCount(1, "dentro del enfriamiento no se reintenta");

        _reloj.Ahora += SenalesIncidenteSeguridad.EsperaTrasFallo + TimeSpan.FromSeconds(1);
        await senales.DocumentoSensibleAbiertoAsync(usuario, Guid.NewGuid());
        _registro.Llamadas.Should().HaveCount(2, "sin desmarcar, la ventana seguiría avisada una hora y la señal se perdería");
        _alerta.Encolados.Should().ContainSingle();
    }

    [Fact]
    public async Task Mas_de_cincuenta_Tenants_se_cortan_y_la_evidencia_guarda_el_total()
    {
        _opciones.UmbralDocumentosSensibles = 60;
        var senales = Crear();
        var usuario = Guid.NewGuid();

        for (var i = 0; i < 60; i++)
            await senales.DocumentoSensibleAbiertoAsync(usuario, Guid.NewGuid());

        var llamada = _registro.Llamadas.Should().ContainSingle().Subject;
        llamada.Tenants.Should().HaveCount(IncidenteSeguridadSospechado.MaximoTenantsAfectados);
        llamada.Evidencia.Should().Contain("\"tenants_afectados_total\":60");
    }

    [Theory]
    [InlineData(0, 300, 5, null)]
    [InlineData(5_000, -1, 5, null)]
    [InlineData(5_000, 300, 0, null)]
    [InlineData(5_000, 300, 5, "http://alertas.example/token")]
    [InlineData(5_000, 300, 5, "alertas.example/token")]
    public void La_configuracion_rechaza_umbrales_no_positivos_y_una_url_de_alerta_sin_https(
        int filas, int documentos, int rechazos, string? url)
        => new OpcionesIncidentesSeguridad
        {
            UmbralFilasExportadas = filas,
            UmbralDocumentosSensibles = documentos,
            UmbralRechazosOtroTenant = rechazos,
            UrlAlertaSaliente = url,
        }.Validar().Should().NotBeEmpty();

    [Fact]
    public void La_configuracion_por_defecto_y_una_url_https_son_validas()
    {
        new OpcionesIncidentesSeguridad().Validar().Should().BeEmpty();
        new OpcionesIncidentesSeguridad { UrlAlertaSaliente = "https://alertas.example/token" }.Validar().Should().BeEmpty();
    }

    [Fact]
    public async Task Un_fallo_al_registrar_no_llega_a_la_operacion_que_disparo_la_senal()
    {
        _registro.Fallar = true;

        await FluentActions.Awaiting(() => Crear().InicioSesionNoReconocidoAsync(Guid.NewGuid(), Guid.NewGuid(), true))
            .Should().NotThrowAsync();
        _alerta.Encolados.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelar_la_peticion_no_impide_registrar_la_senal()
    {
        using var cancelada = new CancellationTokenSource();
        await cancelada.CancelAsync();

        await Crear().InicioSesionNoReconocidoAsync(Guid.NewGuid(), Guid.NewGuid(), true, cancelada.Token);

        _registro.Llamadas.Should().ContainSingle().Which.TokenCancelado.Should().BeFalse();
    }

    [Fact]
    public void El_enlace_de_la_alerta_apunta_a_la_pagina_de_Plataforma_del_incidente()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        new OpcionesIncidentesSeguridad().EnlaceIncidente(id).Should().Be("/plataforma/incidentes/11111111-2222-3333-4444-555555555555");
        new OpcionesIncidentesSeguridad { UrlBasePlataforma = "https://app.example/" }.EnlaceIncidente(id)
            .Should().Be("https://app.example/plataforma/incidentes/11111111-2222-3333-4444-555555555555");
    }

    private sealed record Llamada(SenalIncidenteSeguridad Senal, string Sujeto, IReadOnlyCollection<Guid> Tenants, string Evidencia, TimeSpan Ventana, bool TokenCancelado);

    private sealed class RegistroFalso : IRegistroIncidentesSeguridad
    {
        public List<Llamada> Llamadas { get; } = [];
        public bool SiguienteEsNuevo { get; set; } = true;
        public bool Fallar { get; set; }
        public Guid UltimoId { get; private set; }

        public Task<IncidenteRegistrado> RegistrarAsync(
            SenalIncidenteSeguridad senal, string sujetoClave, IReadOnlyCollection<Guid> tenantsAfectados,
            EvidenciaIncidenteSeguridad evidencia, TimeSpan ventanaDeduplicacion, CancellationToken ct = default)
        {
            Llamadas.Add(new Llamada(senal, sujetoClave, tenantsAfectados, evidencia.ComoJson(), ventanaDeduplicacion, ct.IsCancellationRequested));
            if (Fallar)
                throw new InvalidOperationException("base caída");
            UltimoId = Guid.NewGuid();
            return Task.FromResult(new IncidenteRegistrado(UltimoId, SiguienteEsNuevo));
        }
    }

    private sealed class AlertaFalsa : IAlertaIncidenteSeguridad
    {
        public List<Guid> Encolados { get; } = [];
        public void Encolar(Guid incidenteId) => Encolados.Add(incidenteId);
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public DateTimeOffset Ahora { get; set; } = ahora;

        public override DateTimeOffset GetUtcNow() => Ahora;
    }
}
