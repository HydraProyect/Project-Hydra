using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Empresas;

public class DelegacionYOficioEmpresaDominioTests
{
    private static readonly Guid EmpresaId = Guid.NewGuid();

    [Fact]
    public void Anonimizar_la_delegacion_suprime_el_contacto_y_conserva_el_nombre()
    {
        var delegacion = new DelegacionEmpresa(EmpresaId, "Sevilla");
        delegacion.FijarContactoReclamacion("Ana Ruiz", "ana@example.test", "600000000");

        delegacion.Anonimizar();

        delegacion.Nombre.Should().Be("Sevilla");
        foreach (var campo in DelegacionEmpresa.CamposDeDatosPersonales)
            typeof(DelegacionEmpresa).GetProperty(campo)!.GetValue(delegacion).Should().BeNull(campo);
    }

    [Fact]
    public void Los_campos_personales_de_la_delegacion_son_su_contacto_de_reclamacion()
    {
        DelegacionEmpresa.CamposDeDatosPersonales.Should().BeEquivalentTo(
            nameof(DelegacionEmpresa.ContactoReclamacionNombre), nameof(DelegacionEmpresa.ContactoReclamacionCorreo),
            nameof(DelegacionEmpresa.ContactoReclamacionTelefono));
    }

    [Fact]
    public void El_nombre_se_normaliza_y_la_clave_no_distingue_mayusculas_ni_espacios()
    {
        new OficioEmpresa(EmpresaId, "  Oficial   de 1ª ").Nombre.Should().Be("Oficial de 1ª");
        NombreDeCatalogo.Clave(" OFICIAL  de 1ª").Should().Be(NombreDeCatalogo.Clave("oficial de 1ª"));
        FluentActions.Invoking(() => new OficioEmpresa(EmpresaId, "   ")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Un_trabajador_solo_admite_delegacion_y_oficio_de_su_empresa()
    {
        var trabajador = Trabajador.DeEmpresa(EmpresaId, "Ana", "Ruiz", "12345678Z");

        FluentActions.Invoking(() => trabajador.FijarDelegacionEmpresaYOficio(new DelegacionEmpresa(Guid.NewGuid(), "Ajena"), null))
            .Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => trabajador.FijarDelegacionEmpresaYOficio(null, new OficioEmpresa(Guid.NewGuid(), "Ajeno")))
            .Should().Throw<InvalidOperationException>();

        var delegacion = new DelegacionEmpresa(EmpresaId, "Sevilla");
        var oficio = new OficioEmpresa(EmpresaId, "Soldador");
        trabajador.FijarDelegacionEmpresaYOficio(delegacion, oficio);

        trabajador.DelegacionEmpresaId.Should().Be(delegacion.Id);
        trabajador.OficioEmpresaId.Should().Be(oficio.Id);
        trabajador.Puesto.Should().Be("Soldador");
    }

    [Fact]
    public void Un_trabajador_de_subcontrata_no_tiene_delegacion_ni_oficio()
    {
        var trabajador = Trabajador.DeSubcontrata(Guid.NewGuid(), "Ana", "Ruiz", "12345678Z");

        FluentActions.Invoking(() => trabajador.FijarDelegacionEmpresaYOficio(new DelegacionEmpresa(EmpresaId, "Sevilla"), null))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Anonimizar_al_trabajador_vacia_el_oficio_como_el_puesto_y_conserva_la_delegacion()
    {
        var trabajador = Trabajador.DeEmpresa(EmpresaId, "Ana", "Ruiz", "12345678Z");
        var delegacion = new DelegacionEmpresa(EmpresaId, "Sevilla");
        trabajador.FijarDelegacionEmpresaYOficio(delegacion, new OficioEmpresa(EmpresaId, "Soldador"));

        trabajador.Anonimizar(DateTime.UtcNow);

        trabajador.OficioEmpresaId.Should().BeNull();
        trabajador.Puesto.Should().BeNull();
        trabajador.DelegacionEmpresaId.Should().Be(delegacion.Id);
        Trabajador.CamposDeDatosPersonales.Should().Contain(nameof(Trabajador.OficioEmpresaId));
    }
}
