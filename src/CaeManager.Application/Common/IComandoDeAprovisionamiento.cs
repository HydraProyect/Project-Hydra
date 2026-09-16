namespace CaeManager.Application.Common;

/// <summary>
/// Marcador: este <see cref="ICommand"/> forma parte del flujo de
/// aprovisionamiento inicial de un tenant y, bajo una sesión privilegiada con
/// capacidad <c>Aprovisionamiento</c> acotada al mismo tenant, se le permite
/// escribir (PD-A3).
///
/// Sin miembros a propósito: la interfaz existe solo para que
/// <c>AutorizacionEscrituraBehavior</c> distinga, con <c>is</c>, qué comandos
/// forman parte del alta de tenant de los que no. Lista explícita en vez de
/// heurística de nombre o namespace: un comando nuevo dentro de
/// <c>Empresas</c>/<c>Centros</c>/<c>Trabajadores</c>/<c>Importacion</c> que no
/// implemente esta interfaz NO se beneficia de la escritura acotada, y eso es
/// deliberado — entrar aquí es una decisión, no una consecuencia de la
/// carpeta en la que vive el archivo.
/// </summary>
public interface IComandoDeAprovisionamiento;
