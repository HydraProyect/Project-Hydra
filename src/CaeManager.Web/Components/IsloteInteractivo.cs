namespace CaeManager.Web.Components;

/// <summary>
/// Base obligatoria de todo islote interactivo: un componente sin ruta que es raíz de su
/// circuito, bien porque lleva <c>@rendermode InteractiveServer</c> en su directiva
/// (NavegacionMovil, BotonAsistenteIa…), bien porque MainLayout lo usa con
/// <c>@rendermode="InteractiveServer"</c> (SelectorTema, AnfitrionToasts…). P1-E1c; lo
/// exige <c>IslotesInteractivosConLimiteDeErroresTests</c>.
///
/// <para>
/// Es la mitad del envoltorio; la otra es envolver TODO el marcado del islote en
/// <c>&lt;LimiteDeErrores Islote="this"&gt;</c>. La contención es la de las páginas
/// (<see cref="RaizInteractiva"/>): lo que importa es que un islote de cabecera que falla
/// no se lleve por delante el circuito que comparte con la página y con los demás islotes.
/// Lo que cambia es el aviso: en lugar del estado vacío de región, que no cabe en una
/// cabecera, el límite pinta un aviso compacto en línea con enlace de reintento.
/// </para>
/// </summary>
public abstract class IsloteInteractivo : RaizInteractiva;
