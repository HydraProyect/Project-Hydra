using CaeManager.Application.Integraciones;
using CaeManager.Web.Features.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Recursos;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Tests;

public class AdjuntosCorreoTests
{
    [Fact]
    public async Task Selecciones_repetidas_respetan_el_numero_y_no_abren_el_excedente()
    {
        var carga = new AdjuntosCorreo();
        var adjuntos = new List<AdjuntoParaEnviarDto>();
        for (var i = 0; i < AdjuntosCorreo.MaximoArchivos; i++)
            Assert.Equal(RechazoAdjunto.Ninguno, await carga.AgregarAsync(new([new Archivo(1, [1])]), adjuntos));

        var excedente = new Archivo(1, [1]);
        Assert.Equal(RechazoAdjunto.DemasiadosArchivos, await carga.AgregarAsync(new([excedente]), adjuntos));
        Assert.Equal(AdjuntosCorreo.MaximoArchivos, adjuntos.Count);
        Assert.False(excedente.Abierto);
    }

    [Fact]
    public async Task Una_seleccion_con_mas_archivos_que_el_maximo_no_adjunta_ni_abre_ninguno()
    {
        var carga = new AdjuntosCorreo();
        var adjuntos = new List<AdjuntoParaEnviarDto>();
        var archivos = Enumerable.Range(0, AdjuntosCorreo.MaximoArchivos + 1).Select(_ => new Archivo(1, [1])).ToList();
        Assert.Equal(RechazoAdjunto.DemasiadosArchivos, await carga.AgregarAsync(new(archivos), adjuntos));
        Assert.Empty(adjuntos);
        Assert.All(archivos, a => Assert.False(a.Abierto));
    }

    [Fact]
    public async Task Rechaza_por_presupuesto_antes_de_abrir_y_admite_el_limite_exacto()
    {
        var carga = new AdjuntosCorreo();
        var adjuntos = new List<AdjuntoParaEnviarDto>();
        var maximo = (int)LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes;
        Assert.Equal(RechazoAdjunto.Ninguno, await carga.AgregarAsync(new([new Archivo(maximo - 1, new byte[maximo - 1])]), adjuntos));
        var excedente = new Archivo(2, [1, 2]);
        Assert.Equal(RechazoAdjunto.ExcedeElTotal, await carga.AgregarAsync(new([excedente]), adjuntos));
        Assert.False(excedente.Abierto);
        var ultimo = new Archivo(1, [3]);
        Assert.Equal(RechazoAdjunto.Ninguno, await carga.AgregarAsync(new([ultimo]), adjuntos));
        Assert.Equal(1, ultimo.MaximoSolicitado);
        Assert.Equal(maximo, adjuntos.Sum(a => a.Contenido.LongLength));
    }

    [Fact]
    public async Task Flujo_mayor_que_el_declarado_se_descarta()
    {
        var carga = new AdjuntosCorreo();
        var maximo = (int)LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes;
        var adjuntos = new List<AdjuntoParaEnviarDto> { new("previo", "text/plain", new byte[maximo - 1]) };
        var archivo = new Archivo(1, [1, 2]);
        Assert.Equal(RechazoAdjunto.NoSePudoLeer, await carga.AgregarAsync(new([archivo]), adjuntos));
        Assert.Equal(1, archivo.MaximoSolicitado);
        Assert.Single(adjuntos);
    }

    [Fact]
    public void Cada_rechazo_tiene_un_texto_localizado_y_sin_rechazo_no_hay_texto()
    {
        using var proveedor = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var textos = proveedor.GetRequiredService<IStringLocalizer<TextosErroresConversaciones>>();
        Assert.Null(AdjuntosCorreo.Mensaje(RechazoAdjunto.Ninguno, textos));
        Assert.Contains("3 MB", AdjuntosCorreo.Mensaje(RechazoAdjunto.ExcedeElTotal, textos));
        Assert.Contains("5 archivos", AdjuntosCorreo.Mensaje(RechazoAdjunto.DemasiadosArchivos, textos));
        foreach (var rechazo in new[] { RechazoAdjunto.CargaEnCurso, RechazoAdjunto.NoSePudoLeer })
            Assert.DoesNotContain("ErrorAdjunto", AdjuntosCorreo.Mensaje(rechazo, textos));
    }

    private sealed class Archivo(long tamano, byte[] contenido) : IBrowserFile
    {
        public string Name => "archivo.txt";
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => tamano;
        public string ContentType => "text/plain";
        public bool Abierto { get; private set; }
        public long MaximoSolicitado { get; private set; }
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            Abierto = true;
            MaximoSolicitado = maxAllowedSize;
            return new MemoryStream(contenido);
        }
    }
}
