using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

public class TipoDocumentoTests
{
    [Theory]
    [InlineData(AmbitoAplicacion.Trabajador)]
    [InlineData(AmbitoAplicacion.Cliente)]
    [InlineData(AmbitoAplicacion.Empresa)]
    [InlineData(AmbitoAplicacion.Vehiculo)]
    public void Crea_un_tipo_de_documento_con_el_ambito_indicado(AmbitoAplicacion ambito)
    {
        var tipo = new TipoDocumento("RLC", null, aplicaVencimientoAutomatico: false, orden: 1, ambito);

        tipo.AmbitoAplicacion.Should().Be(ambito);
    }

    [Fact]
    public void Actualizar_no_permite_cambiar_el_ambito()
    {
        var tipo = new TipoDocumento("RLC", null, aplicaVencimientoAutomatico: false, orden: 1, AmbitoAplicacion.Cliente);

        tipo.Actualizar("RLC", null, aplicaVencimientoAutomatico: false, orden: 2,
            requerido: RequisitoDocumental.No, naturaleza: NaturalezaJuridica.RequisitoCliente, notas: null,
            descripcion: null, criteriosValidacion: null, seSolicitaA: null, observaciones: null);

        tipo.AmbitoAplicacion.Should().Be(AmbitoAplicacion.Cliente);
    }

    [Fact]
    public void Requerido_y_naturaleza_nacen_en_el_valor_mas_prudente_y_se_pueden_actualizar()
    {
        var tipo = new TipoDocumento("RLC", null, aplicaVencimientoAutomatico: false, orden: 1, AmbitoAplicacion.Empresa);

        tipo.Requerido.Should().Be(RequisitoDocumental.No);
        tipo.Naturaleza.Should().Be(NaturalezaJuridica.RequisitoCliente,
            "un tipo recién creado no puede presumir una obligación legal ni una práctica del sector que nadie ha verificado");

        tipo.Actualizar("RLC", null, aplicaVencimientoAutomatico: false, orden: 1,
            requerido: RequisitoDocumental.Si, naturaleza: NaturalezaJuridica.PracticaSector, notas: null,
            descripcion: null, criteriosValidacion: null, seSolicitaA: null, observaciones: null);

        tipo.Requerido.Should().Be(RequisitoDocumental.Si);
        tipo.Naturaleza.Should().Be(NaturalezaJuridica.PracticaSector);
    }

    /// <summary>
    /// Los dos ejes son <b>independientes</b>: ese es todo el motivo de partir
    /// el booleano. Un documento puede pedirse siempre sin que ninguna norma
    /// lo exija —el registro de entrega de EPI, el seguro de RC— y hay que
    /// poder expresarlo sin llamarlo ley.
    /// </summary>
    [Theory]
    [InlineData(NaturalezaJuridica.ObligacionLegal)]
    [InlineData(NaturalezaJuridica.PracticaSector)]
    [InlineData(NaturalezaJuridica.RequisitoCliente)]
    public void Un_documento_requerido_puede_tener_cualquier_naturaleza(NaturalezaJuridica naturaleza)
    {
        var tipo = new TipoDocumento("Entrega de EPI", null, aplicaVencimientoAutomatico: false, orden: 1,
            AmbitoAplicacion.Trabajador, RequisitoDocumental.Si, naturaleza);

        tipo.Requerido.Should().Be(RequisitoDocumental.Si);
        tipo.Naturaleza.Should().Be(naturaleza);
        tipo.CuentaParaCumplimiento.Should().BeTrue();
    }

    /// <summary>
    /// La decisión más consecuente de T1: <b>lo condicional no cuenta</b>
    /// mientras no exista la maquinaria que evalúa la condición. Contarlo
    /// pondría en rojo a toda empresa industrial que no pisa una obra —un
    /// falso positivo sistemático— y lo haría en silencio.
    /// </summary>
    [Theory]
    [InlineData(RequisitoDocumental.No, false)]
    [InlineData(RequisitoDocumental.Si, true)]
    [InlineData(RequisitoDocumental.Condicional, false)]
    public void Solo_lo_requerido_cuenta_para_el_cumplimiento(RequisitoDocumental requerido, bool esperado)
    {
        var tipo = new TipoDocumento("REA", null, aplicaVencimientoAutomatico: false, orden: 1,
            AmbitoAplicacion.Empresa, requerido, NaturalezaJuridica.ObligacionCondicionada);

        tipo.CuentaParaCumplimiento.Should().Be(esperado);
    }

    [Fact]
    public void VerificacionIaActiva_empieza_desactivada_y_se_puede_alternar()
    {
        var tipo = new TipoDocumento("Apto médico", 12, aplicaVencimientoAutomatico: true, orden: 1, AmbitoAplicacion.Trabajador);

        tipo.VerificacionIaActiva.Should().BeFalse();

        tipo.EstablecerVerificacionIaActiva(true);

        tipo.VerificacionIaActiva.Should().BeTrue();
    }
}
