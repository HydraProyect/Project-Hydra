using CaeManager.Application.Tenants.Encargo;
using FluentAssertions;
using MediatR;

namespace CaeManager.Application.Tests.Tenants.Encargo
{
    /// <summary>
    /// <b>Codex r2 (#2241): el área de una petición sin marca no sale de su carpeta</b>, sino de su entrada en
    /// <see cref="ActosExcluidosDelEncargo.AreasDePeticionesSinMarca"/>, que la referencia por <c>typeof</c>. Mover la
    /// petición de espacio de nombres no le cambia la entrada, así que no le cambia el área. Usa la sobrecarga interna con
    /// una tabla sintética para no meter tipos de tests en la tabla de Application.
    /// </summary>
    public class AreaDeSinCarpetaTests
    {
        private static readonly Type Antes = typeof(CaeManager.Application.Centros.SimulacionR3.PeticionSinMarcaSimulada);
        private static readonly Type Despues = typeof(CaeManager.Application.Empresas.SimulacionR3.PeticionSinMarcaSimulada);

        [Fact]
        public void La_misma_peticion_en_otro_espacio_de_nombres_conserva_su_area()
        {
            // Control de sensibilidad: las dos carpetas son áreas permitidas distintas; con la regla por espacio de nombres
            // la movida habría pasado de «Centros» a «Empresas».
            Antes.Name.Should().Be(Despues.Name);
            ActosExcluidosDelEncargo.AreasPermitidas.Should().Contain(["Centros", "Empresas"]);

            // La entrada viaja con el tipo: antes del movimiento la tabla nombra Antes; después, el mismo typeof nombra el
            // tipo en su espacio de nombres nuevo, con la misma área.
            ActosExcluidosDelEncargo.AreaDe(Antes, new Dictionary<Type, string> { [Antes] = "Centros" }).Should().Be("Centros");
            ActosExcluidosDelEncargo.AreaDe(Despues, new Dictionary<Type, string> { [Despues] = "Centros" })
                .Should().Be("Centros", "la carpeta Empresas no le cambia el área: manda la entrada de la tabla");
        }

        [Fact]
        public void Sin_entrada_ni_marca_la_carpeta_no_da_area()
        {
            ActosExcluidosDelEncargo.AreaDe(Despues, new Dictionary<Type, string>()).Should().BeNull();
            ActosExcluidosDelEncargo.AreaDe(Despues).Should().BeNull("la tabla real de Application no nombra tipos de tests");
            ActosExcluidosDelEncargo.Excluye(new CaeManager.Application.Empresas.SimulacionR3.PeticionSinMarcaSimulada())
                .Should().BeTrue("sin área, falla cerrado");
        }

        [Fact]
        public void Una_entrada_con_un_area_no_clasificada_no_da_area()
        {
            ActosExcluidosDelEncargo.AreaDe(Antes, new Dictionary<Type, string> { [Antes] = "Inbound" })
                .Should().BeNull("«Inbound» es un módulo, no un área");
        }

        [Fact]
        public void La_marca_manda_sobre_la_tabla_y_la_base_excluida_sobre_las_dos()
        {
            var marcada = typeof(CaeManager.Application.Empresas.SimulacionR3.PeticionMarcadaSimulada);
            ActosExcluidosDelEncargo.AreaDe(marcada, new Dictionary<Type, string> { [marcada] = "Empresas" }).Should().Be("Centros");

            var derivada = typeof(CaeManager.Application.Empresas.SimulacionR3.DerivadaDeExcluidaSimulada);
            ActosExcluidosDelEncargo.AreaDe(derivada, new Dictionary<Type, string> { [derivada] = "Empresas" })
                .Should().Be("Facturacion", "una derivada nunca queda menos restringida que su base");
        }

        [Fact]
        public void La_tabla_real_clasifica_el_caso_de_Codex_r2_por_tipo()
        {
            var centro = typeof(CaeManager.Application.Centros.Queries.ObtenerCentroPorId.ObtenerCentroPorIdQuery);
            ActosExcluidosDelEncargo.AreasDePeticionesSinMarca.Should().ContainKey(centro);
            ActosExcluidosDelEncargo.AreaDe(centro).Should().Be("Centros");
        }
    }
}

namespace CaeManager.Application.Centros.SimulacionR3
{
    public sealed record PeticionSinMarcaSimulada : IRequest;
}

namespace CaeManager.Application.Empresas.SimulacionR3
{
    public sealed record PeticionSinMarcaSimulada : IRequest;

    [AreaDelEncargo("Centros")]
    public sealed record PeticionMarcadaSimulada : IRequest;

    [AreaDelEncargo("Facturacion")]
    public record BaseExcluidaSimulada : IRequest;

    public sealed record DerivadaDeExcluidaSimulada : BaseExcluidaSimulada;
}
