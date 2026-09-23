using CaeManager.Application.Tests.Auditoria;
using CaeManager.Application.Centros.Queries.ObtenerCredencialCanalGestion;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Centros;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// La contraseña de una Plataforma CAE solo sale hacia la interfaz por esta
/// consulta, y solo con alcance de GESTIÓN sobre el Centro — no con el de
/// lectura (REC-153): un usuario de portal (rol Cliente) ve sus Centros, pero
/// no opera sobre ellos.
/// </summary>
public class ObtenerCredencialCanalGestionQueryTests
{
    private static (CentrosQueryContextFalso Contexto, Guid CentroId, CanalGestionDocumental Canal) Escenario()
    {
        var centroId = Guid.NewGuid();
        var canal = CanalGestionDocumental.DePlataforma(
            centroId, "Gestión general", Guid.NewGuid(), "https://portal.example", "marta.gestora", "s3creta");
        var contexto = new CentrosQueryContextFalso();
        contexto.ListaCanalesGestionDocumental.Add(canal);
        return (contexto, centroId, canal);
    }

    [Fact]
    public async Task Con_alcance_de_gestion_devuelve_usuario_y_contrasena_del_canal()
    {
        var (contexto, centroId, canal) = Escenario();
        var handler = new ObtenerCredencialCanalGestionQueryHandler(
            contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centroId]), new RegistroAccesoDatoSensibleFalso());

        var resultado = await handler.Handle(new ObtenerCredencialCanalGestionQuery(centroId, canal.Id), CancellationToken.None);

        resultado.Should().NotBeNull("un Gestor CAE con el Centro en su cartera puede copiar sus credenciales");
        resultado!.Usuario.Should().Be("marta.gestora");
        resultado.Contrasena.Should().Be("s3creta");
    }

    /// <summary>
    /// El caso que separa gestión de lectura: el Centro está en la cartera de
    /// LECTURA del usuario de portal, pero no en la de GESTIÓN.
    /// </summary>
    [Fact]
    public async Task Un_usuario_de_portal_con_el_centro_en_su_cartera_de_lectura_no_recibe_la_contrasena()
    {
        var (contexto, centroId, canal) = Escenario();
        var handler = new ObtenerCredencialCanalGestionQueryHandler(
            contexto,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centroId], centroIdsParaGestion: []), new RegistroAccesoDatoSensibleFalso());

        var resultado = await handler.Handle(new ObtenerCredencialCanalGestionQuery(centroId, canal.Id), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Un_centro_fuera_de_la_cartera_responde_como_si_no_existiera()
    {
        var (contexto, centroId, canal) = Escenario();
        var handler = new ObtenerCredencialCanalGestionQueryHandler(
            contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]), new RegistroAccesoDatoSensibleFalso());

        var resultado = await handler.Handle(new ObtenerCredencialCanalGestionQuery(centroId, canal.Id), CancellationToken.None);

        resultado.Should().BeNull();
    }

    /// <summary>Autorizar un Centro no autoriza el canal de otro: el Id de canal se ata al Centro pedido.</summary>
    [Fact]
    public async Task El_id_de_un_canal_de_otro_centro_no_se_resuelve_aunque_el_centro_pedido_este_en_la_cartera()
    {
        var (contexto, _, canalAjeno) = Escenario();
        var centroPropio = Guid.NewGuid();
        var handler = new ObtenerCredencialCanalGestionQueryHandler(
            contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centroPropio]), new RegistroAccesoDatoSensibleFalso());

        var resultado = await handler.Handle(new ObtenerCredencialCanalGestionQuery(centroPropio, canalAjeno.Id), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Un_canal_por_email_no_tiene_credenciales_que_dar()
    {
        var centroId = Guid.NewGuid();
        var canal = CanalGestionDocumental.PorEmail(centroId, "Gestión general", "prl@centro.com", "Responsable PRL");
        var contexto = new CentrosQueryContextFalso();
        contexto.ListaCanalesGestionDocumental.Add(canal);
        var handler = new ObtenerCredencialCanalGestionQueryHandler(contexto, new AlcanceDatosServiceFalso(), new RegistroAccesoDatoSensibleFalso());

        var resultado = await handler.Handle(new ObtenerCredencialCanalGestionQuery(centroId, canal.Id), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public void La_consulta_esta_marcada_como_consulta_de_secretos_de_tenant()
    {
        typeof(IConsultaDeSecretosDeTenant).IsAssignableFrom(typeof(ObtenerCredencialCanalGestionQuery))
            .Should().BeTrue("una sesión privilegiada de plataforma no puede llevarse la contraseña");
    }
}
