using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lógica pura de las dos piezas de la revisión 1:1 (incremento 2): la regla de «revisada» de la respuesta sugerida (E18,
/// <c>UMBRAL_REVISION</c> de la maqueta) y el interlocutor al que se pregunta por la nota fija (E21).
/// </summary>
public class RespuestaSugeridaLogicaTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("hola", "hola", 0)]
    [InlineData("hola", "hala", 1)]
    [InlineData("kitten", "sitting", 3)]
    public void La_distancia_de_edicion_es_la_de_Levenshtein(string a, string b, int esperada) =>
        RevisionRespuestaSugerida.DistanciaEdicion(a, b).Should().Be(esperada);

    [Fact]
    public void Quince_cambios_no_bastan_y_dieciseis_si()
    {
        var original = new string('a', 100);
        var quince = new string('b', 15) + new string('a', 85);
        var dieciseis = new string('b', 16) + new string('a', 84);

        RevisionRespuestaSugerida.EditadaLoSuficiente(quince, original).Should().BeFalse("una letra suelta no basta: el umbral es mayor que 15");
        RevisionRespuestaSugerida.EditadaLoSuficiente(dieciseis, original).Should().BeTrue();
    }

    [Fact]
    public void Una_diferencia_de_longitud_mayor_que_el_umbral_cuenta_como_editada()
    {
        RevisionRespuestaSugerida.EditadaLoSuficiente(string.Empty, new string('x', 16)).Should().BeTrue();
        RevisionRespuestaSugerida.EditadaLoSuficiente(null, new string('x', 15)).Should().BeFalse();
    }

    [Fact]
    public void La_banda_acotada_da_el_mismo_resultado_que_la_distancia_completa()
    {
        var azar = new Random(20261111);
        for (var i = 0; i < 200; i++)
        {
            var a = Aleatoria(azar, azar.Next(0, 60));
            var b = Mutada(azar, a, azar.Next(0, 25));
            var exacta = Levenshtein(a, b);

            RevisionRespuestaSugerida.DistanciaEdicion(a, b).Should().Be(Math.Min(exacta, RevisionRespuestaSugerida.UmbralEdicion + 1),
                $"'{a}' frente a '{b}'");
        }
    }

    [Fact]
    public void Un_texto_enorme_se_evalua_sin_recorrer_la_tabla_entera()
    {
        var grande = new string('a', 300_000);
        var otro = grande + "bbbbbbbbbbbbbbbbbbbb";

        RevisionRespuestaSugerida.EditadaLoSuficiente(otro, grande).Should().BeTrue();
        RevisionRespuestaSugerida.EditadaLoSuficiente(grande, grande).Should().BeFalse();
    }

    private static readonly Guid ClienteId = Guid.NewGuid();
    private static readonly Guid EmpresaId = Guid.NewGuid();

    private static ConversacionDetalleDto Detalle(
        string remitente, Guid? clienteId, IReadOnlyList<ParticipanteDetalleDto>? participantes = null,
        DireccionMensaje direccion = DireccionMensaje.Entrante)
    {
        var mensaje = new MensajeDetalleDto(
            Guid.NewGuid(), direccion, CanalConversacion.Correo, remitente, "<p>Hola</p>", DateTime.UtcNow,
            [], null, []);
        return new ConversacionDetalleDto(
            Guid.NewGuid(), clienteId, null, "Asunto", EstadoConversacion.Abierta, null, null, DateTime.UtcNow,
            [mensaje], participantes ?? [], [], []);
    }

    [Fact]
    public void Sin_correo_en_el_ultimo_entrante_no_hay_interlocutor()
    {
        InterlocutorNotaFija.De(Detalle("+34 600 000 000", ClienteId)).Should().BeNull("un teléfono no es una dirección de la Agenda");
        InterlocutorNotaFija.De(Detalle("laura@x.es", ClienteId, direccion: DireccionMensaje.Saliente)).Should().BeNull(
            "solo cuenta lo que escribió el contacto");
    }

    [Fact]
    public void Sin_participante_con_entidad_pregunta_en_la_agenda_del_cliente_empresarial()
    {
        var interlocutor = InterlocutorNotaFija.De(Detalle("Laura Pérez <Laura@Obra.ES>", ClienteId));

        interlocutor.Should().Be(new InterlocutorNotaFija.Interlocutor(TipoPropietarioAgenda.Cliente, ClienteId, "laura@obra.es"));
    }

    [Fact]
    public void La_entidad_que_el_hilo_sabe_del_participante_manda_sobre_el_cliente()
    {
        var participantes = new[]
        {
            new ParticipanteDetalleDto(Guid.NewGuid(), "laura@obra.es", RolParticipante.De, TipoParticipanteOrigen.Empresa, EmpresaId)
        };

        var interlocutor = InterlocutorNotaFija.De(Detalle("laura@obra.es", ClienteId, participantes));

        interlocutor.Should().Be(new InterlocutorNotaFija.Interlocutor(TipoPropietarioAgenda.Empresa, EmpresaId, "laura@obra.es"));
    }

    [Fact]
    public void Sin_cliente_ni_entidad_no_hay_a_quien_preguntar() =>
        InterlocutorNotaFija.De(Detalle("laura@obra.es", null)).Should().BeNull();

    private static string Aleatoria(Random azar, int largo) =>
        new(Enumerable.Range(0, largo).Select(_ => (char)('a' + azar.Next(0, 3))).ToArray());

    private static string Mutada(Random azar, string texto, int cambios)
    {
        var letras = texto.ToList();
        for (var i = 0; i < cambios; i++)
        {
            switch (azar.Next(0, 3))
            {
                case 0 when letras.Count > 0: letras.RemoveAt(azar.Next(0, letras.Count)); break;
                case 1: letras.Insert(azar.Next(0, letras.Count + 1), (char)('a' + azar.Next(0, 3))); break;
                case 2 when letras.Count > 0: letras[azar.Next(0, letras.Count)] = (char)('a' + azar.Next(0, 3)); break;
            }
        }

        return new string(letras.ToArray());
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
