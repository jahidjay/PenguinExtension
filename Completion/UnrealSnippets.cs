using System.Collections.Generic;
using System.Linq;

namespace PenguinExtention.Completion
{
    /// <summary>
    /// Ready-made full lines offered when typing the start of a reflection macro or common UE statement
    /// ("UPRO" → several complete UPROPERTY(...) lines). Inserted as plain text; <see cref="Snippet.CaretBack"/>
    /// places the caret inside the first thing you normally edit (a quoted string or empty parentheses).
    /// </summary>
    internal static class UnrealSnippets
    {
        internal sealed class Snippet
        {
            public string Keyword;      // matched against what is typed
            public string Text;         // inserted text
            public string Description;
            public int CaretBack;       // characters from the end where the caret lands (0 = end)

            public Snippet(string keyword, string text, string description, int caretBack = 0)
            {
                Keyword = keyword; Text = text; Description = description; CaretBack = caretBack;
            }
        }

        public static readonly Snippet[] All =
        {
            // UPROPERTY
            new Snippet("UPROPERTY", "UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = \"Default\")", "Editable everywhere, read/write in Blueprint.", 2),
            new Snippet("UPROPERTY", "UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category = \"Components\")", "Components: visible in the editor, read-only in Blueprint.", 2),
            new Snippet("UPROPERTY", "UPROPERTY(EditDefaultsOnly, BlueprintReadOnly, Category = \"Default\")", "Tuning value set on the class defaults (Blueprint subclass).", 2),
            new Snippet("UPROPERTY", "UPROPERTY(EditInstanceOnly, Category = \"Default\")", "Set per placed instance in the level.", 2),
            new Snippet("UPROPERTY", "UPROPERTY(BlueprintReadOnly, Category = \"State\")", "Runtime state Blueprint can read.", 2),
            new Snippet("UPROPERTY", "UPROPERTY(Replicated)", "Replicated to clients (add to GetLifetimeReplicatedProps)."),
            new Snippet("UPROPERTY", "UPROPERTY(ReplicatedUsing = OnRep_)", "Replicated with an OnRep_ notify function.", 1),
            new Snippet("UPROPERTY", "UPROPERTY(Transient)", "Not saved; reset on load."),
            new Snippet("UPROPERTY", "UPROPERTY(Config)", "Loaded from the class's config .ini."),
            new Snippet("UPROPERTY", "UPROPERTY(meta = (BindWidget))", "UMG: bound to the widget with the same name."),
            new Snippet("UPROPERTY", "UPROPERTY()", "Reflected for GC/serialization only.", 1),
            // UFUNCTION
            new Snippet("UFUNCTION", "UFUNCTION(BlueprintCallable, Category = \"Default\")", "Callable from Blueprint (exec node).", 2),
            new Snippet("UFUNCTION", "UFUNCTION(BlueprintPure, Category = \"Default\")", "Pure Blueprint getter (no exec pins).", 2),
            new Snippet("UFUNCTION", "UFUNCTION(BlueprintImplementableEvent, Category = \"Default\")", "Declared in C++, implemented in Blueprint.", 2),
            new Snippet("UFUNCTION", "UFUNCTION(BlueprintNativeEvent, Category = \"Default\")", "C++ default (Name_Implementation), overridable in Blueprint.", 2),
            new Snippet("UFUNCTION", "UFUNCTION(Server, Reliable)", "Client → server RPC (implement Name_Implementation)."),
            new Snippet("UFUNCTION", "UFUNCTION(Client, Reliable)", "Server → owning client RPC."),
            new Snippet("UFUNCTION", "UFUNCTION(NetMulticast, Unreliable)", "Server → all clients RPC."),
            new Snippet("UFUNCTION", "UFUNCTION(Exec)", "Console command (on player controller, pawn, HUD, cheat manager, game mode)."),
            new Snippet("UFUNCTION", "UFUNCTION()", "Reflected (needed for delegates / timers by name).", 1),
            // Types
            new Snippet("UCLASS", "UCLASS()", "Reflected UObject class.", 1),
            new Snippet("UCLASS", "UCLASS(Blueprintable, BlueprintType)", "Can be subclassed and used as a variable type in Blueprint."),
            new Snippet("UCLASS", "UCLASS(Abstract)", "Cannot be instantiated directly."),
            new Snippet("UCLASS", "UCLASS(ClassGroup = (Custom), meta = (BlueprintSpawnableComponent))", "Actor component addable in the Blueprint editor."),
            new Snippet("USTRUCT", "USTRUCT(BlueprintType)", "Struct usable as a Blueprint variable."),
            new Snippet("USTRUCT", "USTRUCT()", "Reflected struct.", 1),
            new Snippet("UENUM", "UENUM(BlueprintType)", "Enum usable in Blueprint (enum class E… : uint8)."),
            new Snippet("UENUM", "UENUM()", "Reflected enum.", 1),
            new Snippet("UINTERFACE", "UINTERFACE(MinimalAPI, Blueprintable)", "Interface declaration (pair with class I…)."),
            new Snippet("GENERATED_BODY", "GENERATED_BODY()", "Required first line in every reflected class/struct."),
            // Statements
            new Snippet("UE_LOG", "UE_LOG(LogTemp, Warning, TEXT(\"\"));", "Log to the Output Log.", 4),
            new Snippet("UE_LOG", "UE_LOG(LogTemp, Log, TEXT(\"%s\"), *GetName());", "Log with a formatted value.", 0),
            new Snippet("GEngine", "GEngine->AddOnScreenDebugMessage(-1, 5.f, FColor::Yellow, TEXT(\"\"));", "Print on screen.", 4),
            new Snippet("check", "check();", "Assert (development builds; crashes on failure).", 2),
            new Snippet("ensure", "ensure();", "Soft assert: logs once and continues.", 2),
            new Snippet("DECLARE_DYNAMIC_MULTICAST_DELEGATE", "DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnSomething, float, Value);", "Blueprint-assignable event delegate type.", 0),
            new Snippet("DECLARE_DELEGATE", "DECLARE_DELEGATE_OneParam(FOnSomething, float);", "Single-bound C++ delegate type."),
            new Snippet("CreateDefaultSubobject", "CreateDefaultSubobject<UStaticMeshComponent>(TEXT(\"Mesh\"));", "Create a component in the constructor.", 4),
            new Snippet("GetWorldTimerManager", "GetWorldTimerManager().SetTimer(TimerHandle, this, &ThisClass::OnTimer, 1.0f, true);", "Repeating timer."),
            new Snippet("DOREPLIFETIME", "DOREPLIFETIME(ThisClass, );", "Register a replicated property.", 2),
        };

        /// <summary>Snippets whose keyword matches <paramref name="typed"/> (at least 2 characters).</summary>
        public static IEnumerable<Snippet> Matching(string typed)
        {
            if (string.IsNullOrEmpty(typed) || typed.Length < 2) return Enumerable.Empty<Snippet>();
            return All.Where(s => PenguinMatcher.Score(typed, s.Keyword) >= 0 && PenguinMatcher.Score(typed, s.Keyword) <= 3);
        }
    }
}
