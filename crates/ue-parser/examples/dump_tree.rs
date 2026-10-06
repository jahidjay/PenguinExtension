//! Dev utility: dump the tree-sitter-cpp S-expression for a UE header snippet.
//! Run: cargo run -p ue-parser --example dump_tree [path/to/file.h]

fn main() {
    let src = match std::env::args().nth(1) {
        Some(path) => std::fs::read_to_string(path).expect("read input file"),
        None => DEFAULT_SNIPPET.to_string(),
    };
    let mut parser = tree_sitter::Parser::new();
    parser
        .set_language(&tree_sitter_cpp::language())
        .expect("load C++ grammar");
    let tree = parser.parse(&src, None).expect("parse");
    println!("{}", tree.root_node().to_sexp());
}

const DEFAULT_SNIPPET: &str = r#"
UCLASS(Blueprintable, meta=(DisplayName="My Actor"))
class MYGAME_API AMyActor : public AActor, public IMyInterface
{
    GENERATED_BODY()

public:
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Stats", meta=(ClampMin="0.0", ClampMax="100.0"))
    float Health;

    UFUNCTION(BlueprintCallable, Category="Combat")
    void ApplyDamage(float Amount, AActor* Causer);
};

USTRUCT(BlueprintType)
struct FMyData
{
    GENERATED_BODY()

    UPROPERTY(EditAnywhere)
    int32 Count;
};

UENUM(BlueprintType)
enum class EMyState : uint8
{
    Idle UMETA(DisplayName="Idle State"),
    Active
};

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnHealthChanged, float, NewHealth);
"#;
