using CaeManager.Domain.Centros;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Centros;

public class CanalGestionDocumentalTests
{
    [Fact]
    public void Crea_un_canal_de_plataforma_con_su_etiqueta_de_proposito()
    {
        var centroId = Guid.NewGuid();

        var canal = CanalGestionDocumental.DePlataforma(
            centroId, "Trabajadores extranjeros", "CTAIMA CAE", "https://ctaima.example", "usuario", "secreto");

        canal.CentroId.Should().Be(centroId);
        canal.Tipo.Should().Be(TipoCanalGestion.Plataforma);
        canal.EtiquetaProposito.Should().Be("Trabajadores extranjeros");
        canal.NombrePlataforma.Should().Be("CTAIMA CAE");
        canal.EsPrincipal.Should().BeFalse();
    }

    [Fact]
    public void Crea_un_canal_por_email_con_su_etiqueta_de_proposito()
    {
        var canal = CanalGestionDocumental.PorEmail(
            Guid.NewGuid(), "Gestión general", "prl@centro.com", "Responsable PRL");

        canal.Tipo.Should().Be(TipoCanalGestion.Email);
        canal.EtiquetaProposito.Should().Be("Gestión general");
        canal.EmailsDestinatarios.Should().Be("prl@centro.com");
    }

    [Fact]
    public void Exige_etiqueta_de_proposito()
    {
        var accion = () => CanalGestionDocumental.DePlataforma(Guid.NewGuid(), "   ", "CTAIMA CAE", null, null, null);

        accion.Should().Throw<ArgumentException>().WithMessage("*etiqueta de propósito es obligatoria*");
    }

    [Fact]
    public void Recorta_la_etiqueta_de_proposito()
    {
        var canal = CanalGestionDocumental.PorEmail(Guid.NewGuid(), "  Gestión general  ", "prl@centro.com", null);

        canal.EtiquetaProposito.Should().Be("Gestión general");
    }

    [Fact]
    public void Rechaza_una_etiqueta_de_proposito_demasiado_larga()
    {
        var larga = new string('x', CanalGestionDocumental.LongitudMaximaEtiquetaProposito + 1);

        var accion = () => CanalGestionDocumental.PorEmail(Guid.NewGuid(), larga, "prl@centro.com", null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Marca_y_desmarca_el_canal_principal()
    {
        var canal = CanalGestionDocumental.PorEmail(Guid.NewGuid(), "Gestión general", "prl@centro.com", null);

        canal.MarcarComoPrincipal();
        canal.EsPrincipal.Should().BeTrue();

        canal.DejarDeSerPrincipal();
        canal.EsPrincipal.Should().BeFalse();
    }

    [Fact]
    public void Actualizar_plataforma_cambia_tambien_la_etiqueta_de_proposito()
    {
        var canal = CanalGestionDocumental.DePlataforma(
            Guid.NewGuid(), "Gestión general", "CTAIMA CAE", null, "usuario", "secreto");

        canal.ActualizarPlataforma("Trabajadores extranjeros", "Dokify", "https://dokify.example", "otra nota");

        canal.EtiquetaProposito.Should().Be("Trabajadores extranjeros");
        canal.NombrePlataforma.Should().Be("Dokify");
        canal.Usuario.Should().Be("usuario", "actualizar la ficha no toca las credenciales");
    }

    [Fact]
    public void No_se_puede_actualizar_un_canal_de_email_como_si_fuera_plataforma()
    {
        var canal = CanalGestionDocumental.PorEmail(Guid.NewGuid(), "Gestión general", "prl@centro.com", null);

        var accion = () => canal.ActualizarPlataforma("Gestión general", "CTAIMA CAE", null, null);

        accion.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Exige_un_centro()
    {
        var accion = () => CanalGestionDocumental.PorEmail(Guid.Empty, "Gestión general", "prl@centro.com", null);

        accion.Should().Throw<ArgumentException>().WithMessage("*debe pertenecer a un centro*");
    }
}
