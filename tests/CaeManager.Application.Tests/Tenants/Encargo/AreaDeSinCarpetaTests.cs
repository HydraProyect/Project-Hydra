using CaeManager.Application.Tenants.Encargo;
using FluentAssertions;
using MediatR;

namespace CaeManager.Application.Tests.Tenants.Encargo
{
    /// <summary>
    /// <b>Codex r2 (#2241), decisión A: el área de una petición sale de su marca, no de su carpeta.</b> La marca vive en
    /// el propio tipo, así que mover la petición de espacio de nombres no le cambia el área, y sin marca la carpeta no
    /// da área (la petición queda excluida). No hay tabla central <c>typeof</c>: referenciaba Outbound desde el Núcleo y
    /// rompía el ratchet de M0b.
    /// </summary>
    public class AreaDeSinCarpetaTests
    {
        private static readonly Type Antes = typeof(CaeManager.Application.Centros.SimulacionR3.PeticionMarcadaSimulada);
        private static readonly Type Despues = typeof(CaeManager.Application.Empresas.SimulacionR3.PeticionMarcadaSimulada);

        [Fact]
        public void La_misma_peticion_marcada_en_otro_espacio_de_nombres_conserva_su_area()
        {
            // Control de sensibilidad: las dos carpetas son áreas permitidas distintas; con la regla por espacio de nombres
            // la movida habría pasado de «Centros» a «Empresas».
            Antes.Name.Should().Be(Despues.Name);
            Antes.Namespace.Should().NotBe(Despues.Namespace);
            ActosExcluidosDelEncargo.AreasPermitidas.Should().Contain(["Centros", "Empresas"]);

            ActosExcluidosDelEncargo.AreaDe(Antes).Should().Be("Centros");
            ActosExcluidosDelEncargo.AreaDe(Despues).Should().Be("Centros", "la carpeta Empresas no le cambia el área: manda la marca");
        }

        [Fact]
        public void Sin_marca_la_carpeta_no_da_area()
        {
            var sinMarca = typeof(CaeManager.Application.Empresas.SimulacionR3.PeticionSinMarcaSimulada);
            ActosExcluidosDelEncargo.AreaDe(sinMarca).Should().BeNull("la carpeta Empresas no da área");
            ActosExcluidosDelEncargo.Excluye(new CaeManager.Application.Empresas.SimulacionR3.PeticionSinMarcaSimulada())
                .Should().BeTrue("sin área, falla cerrado");
        }

        [Fact]
        public void Una_marca_con_un_area_no_clasificada_no_da_area()
        {
            ActosExcluidosDelEncargo.AreaDe(typeof(CaeManager.Application.Empresas.SimulacionR3.PeticionConModuloComoAreaSimulada))
                .Should().BeNull("«Inbound» es un módulo, no un área");
        }

        [Fact]
        public void La_base_excluida_manda_sobre_la_marca_de_la_derivada()
        {
            var derivada = typeof(CaeManager.Application.Empresas.SimulacionR3.DerivadaDeExcluidaSimulada);
            ActosExcluidosDelEncargo.AreaDe(derivada).Should().Be("Facturacion", "una derivada nunca queda menos restringida que su base");
        }

        [Fact]
        public void La_marca_real_clasifica_el_caso_de_Codex_r2_por_tipo()
        {
            var centro = typeof(CaeManager.Application.Centros.Queries.ObtenerCentroPorId.ObtenerCentroPorIdQuery);
            centro.GetCustomAttributes(typeof(AreaDelEncargoAttribute), inherit: false).Should().ContainSingle();
            ActosExcluidosDelEncargo.AreaDe(centro).Should().Be("Centros");
        }
    }
}

namespace CaeManager.Application.Centros.SimulacionR3
{
    [AreaDelEncargo("Centros")]
    public sealed record PeticionMarcadaSimulada : IRequest;
}

namespace CaeManager.Application.Empresas.SimulacionR3
{
    public sealed record PeticionSinMarcaSimulada : IRequest;

    [AreaDelEncargo("Centros")]
    public sealed record PeticionMarcadaSimulada : IRequest;

    [AreaDelEncargo("Inbound")]
    public sealed record PeticionConModuloComoAreaSimulada : IRequest;

    [AreaDelEncargo("Facturacion")]
    public record BaseExcluidaSimulada : IRequest;

    public sealed record DerivadaDeExcluidaSimulada : BaseExcluidaSimulada;
}
