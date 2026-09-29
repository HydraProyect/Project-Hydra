using CaeManager.Application.Common;

namespace CaeManager.Web.Features.AsistenteIa;

public partial class BotonAsistenteIa
{
    /// <summary>«Asistente TALVEG» cuando el agente está disponible; «Pregúntale a TALVEG» si solo hay consultas.</summary>
    private string TituloBoton => Disponibilidad.AgenteDisponible
        ? Textos["TituloAgente", Marca.Nombre]
        : Textos["Titulo", Marca.Nombre];
}
