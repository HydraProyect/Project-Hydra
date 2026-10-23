using System.Globalization;
using System.Resources;
using CaeManager.Web.Features.Clientes.Recursos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public class Cliente360TextosTests
{
    private static readonly ResourceManager Recursos = new(typeof(TextosClientes));

    [Theory]
    [InlineData("InformacionGuardada", "Información guardada", "Informació desada")]
    [InlineData("NotaInternaGuardada", "Nota interna guardada", "Nota interna desada")]
    [InlineData("ToleranciaGuardada", "Tolerancia de documentos guardada", "Tolerància de documents desada")]
    [InlineData("CifCopiado", "CIF {0} copiado al portapapeles", "CIF {0} copiat al porta-retalls")]
    [InlineData("CodigoClienteCopiado", "Código de cliente copiado al portapapeles", "Codi de client copiat al porta-retalls")]
    [InlineData("ToleranciaIntro", "Días que un documento vencido sigue admitiéndose en los centros de este Cliente.",
        "Dies que un document vençut continua admetent-se als centres d'aquest Client.")]
    [InlineData("CentroTodoValidado", "Todo validado en la plataforma", "Tot validat a la plataforma")]
    [InlineData("DetalleBloqueaAccesoUno", "1 documento bloquea el acceso", "1 document bloqueja l'accés")]
    [InlineData("DetalleBloqueanAccesoVarios", "{0} documentos bloquean el acceso", "{0} documents bloquegen l'accés")]
    [InlineData("DetallePendienteValidacionUno", "1 pendiente de validación", "1 pendent de validació")]
    [InlineData("DetallePendientesValidacionVarios", "{0} pendientes de validación", "{0} pendents de validació")]
    [InlineData("ToastClienteBaja", "{0} dado de baja", "{0} donat de baixa")]
    [InlineData("ToastClienteRestaurado", "Baja del Cliente deshecha", "Baixa del Client desfeta")]
    [InlineData("ToastAccionDeshacer", "Deshacer", "Desfer")]
    public void Los_textos_de_la_maqueta_tienen_su_version_espanola_y_catalana(string clave, string castellano, string catalan)
    {
        Recursos.GetString(clave, CultureInfo.GetCultureInfo("es-ES")).Should().Be(castellano);
        Recursos.GetString(clave, CultureInfo.GetCultureInfo("ca-ES")).Should().Be(catalan);
    }
}