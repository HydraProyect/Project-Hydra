using CaeManager.Application.Visitas.GestionPorCorreo;
using CaeManager.Application.Visitas.Queries.ObtenerAvisoVisita;
using CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;
using CaeManager.Domain.Centros;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// P1-X1: cuándo un Centro se gestiona por correo (<see cref="CanalCorreoDeCentro"/>) y
/// el texto de la solicitud de acceso. La autorización y el zip con datos reales los
/// prueba la integración (<c>PaqueteDocumentalVisitaCorreoTests</c>).
/// </summary>
public class SolicitudAccesoCorreoComposicionTests
{
    private static CanalCorreoDeCentro.CanalCandidato Email(bool principal = false, string? emails = "acceso@centro.es", string? contacto = "Marta") =>
        new(TipoCanalGestion.Email, principal, emails, contacto, Guid.NewGuid());

    private static CanalCorreoDeCentro.CanalCandidato Plataforma(bool principal = false) =>
        new(TipoCanalGestion.Plataforma, principal, null, null, Guid.NewGuid());

    [Fact]
    public void Un_centro_cuyo_canal_principal_es_el_correo_se_gestiona_por_correo()
    {
        var canal = CanalCorreoDeCentro.Elegir([Plataforma(), Email(principal: true, emails: " acceso@centro.es ")]);

        canal.Should().Be(new CanalCorreoCentro("acceso@centro.es", "Marta"));
    }

    [Fact]
    public void Un_centro_cuyo_canal_principal_es_una_plataforma_no_se_gestiona_por_correo()
    {
        CanalCorreoDeCentro.Elegir([Plataforma(principal: true), Email()]).Should().BeNull();
    }

    [Fact]
    public void Sin_principal_solo_cuenta_el_correo_si_no_hay_ninguna_plataforma()
    {
        CanalCorreoDeCentro.Elegir([Email()]).Should().NotBeNull();
        CanalCorreoDeCentro.Elegir([Email(), Plataforma()]).Should().BeNull("con portal y correo y sin principal, el canal no es «el correo»");
    }

    [Fact]
    public void Un_canal_de_correo_sin_destinatarios_no_cuenta_y_un_centro_sin_canales_tampoco()
    {
        CanalCorreoDeCentro.Elegir([Email(principal: true, emails: "  ")]).Should().BeNull();
        CanalCorreoDeCentro.Elegir([]).Should().BeNull();
    }

    private static DatosSolicitudAccesoCorreo Datos(string? contacto = "Marta", DateOnly? fin = null, TimeOnly? hora = null) =>
        new("Oficina Huesca",
            contacto,
            new DateOnly(2026, 9, 28),
            fin ?? new DateOnly(2026, 9, 28),
            hora,
            [new TrabajadorAvisoVisita("Ana López", "Instalaciones Norte SL"), new TrabajadorAvisoVisita("Luis Pérez", "Subcontrata Sur SL")],
            "Mantenimientos Ebro SL");

    [Fact]
    public void La_solicitud_pide_acceso_con_centro_fechas_hora_quien_acude_y_el_zip_adjunto()
    {
        var (asunto, cuerpo) = ObtenerSolicitudAccesoCorreoQueryHandler.Componer(Datos(hora: new TimeOnly(8, 30)));

        asunto.Should().Be("Solicitud de acceso — Oficina Huesca — 28/09/2026");
        var lineas = cuerpo.Split('\n');
        lineas[0].Should().Be("Buenos días, Marta:");
        lineas.Should().Contain("Os solicitamos acceso a Oficina Huesca para la siguiente visita.");
        lineas.Should().Contain("Fecha: 28/09/2026");
        lineas.Should().Contain("Hora estimada de llegada: 08:30");
        lineas.Should().Contain("Acuden:");
        lineas.Should().Contain("- Ana López (Instalaciones Norte SL)");
        lineas.Should().Contain("- Luis Pérez (Subcontrata Sur SL)");
        lineas.Should().Contain(l => l.Contains("archivo ZIP"));
        lineas[^1].Should().Be("Mantenimientos Ebro SL");
        cuerpo.Should().NotContain("\r", "el texto se copia igual en cualquier sistema");
    }

    [Fact]
    public void Sin_contacto_ni_hora_y_con_varios_dias_la_solicitud_lo_dice()
    {
        var (_, cuerpo) = ObtenerSolicitudAccesoCorreoQueryHandler.Componer(Datos(contacto: null, fin: new DateOnly(2026, 9, 30)));

        var lineas = cuerpo.Split('\n');
        lineas[0].Should().Be("Buenos días:");
        lineas.Should().Contain("Fechas: del 28/09/2026 al 30/09/2026");
        lineas.Should().Contain("Hora estimada de llegada: por confirmar");
    }
}
