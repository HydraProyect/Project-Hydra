namespace CaeManager.Web.Components;

/// <summary>
/// Base obligatoria de toda página con <c>@rendermode InteractiveServer</c> (P1-E1b;
/// lo exige <c>PaginasInteractivasConLimiteDeErroresTests</c>). Es la mitad del envoltorio
/// común; la otra mitad es envolver TODO el marcado de la página en
/// <c>&lt;LimiteDeErrores Pagina="this"&gt;</c>, que muestra el aviso de región con
/// reintento. La contención y el porqué están en <see cref="RaizInteractiva"/>.
/// </summary>
public abstract class PaginaInteractiva : RaizInteractiva;
