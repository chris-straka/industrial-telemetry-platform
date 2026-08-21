{
  get_syntax() {
    local file="$1"
    case "$file" in
      infra/*|*.tf|*.tfvars)             echo "hcl" ;;
      chart/*|monitoring/*|*.yaml|*.yml) echo "yaml" ;;
      *.cs)                              echo "csharp" ;;
      *.tsx|*.ts)                        echo "typescript" ;;
      *.jsx|*.js)                        echo "javascript" ;;
      *.css)                             echo "css" ;;
      *.json|.*rc)                       echo "json" ;;
      *.csproj|*.xml)                    echo "xml" ;;
      *Dockerfile*)                      echo "dockerfile" ;;
      Makefile|*.mk)                     echo "makefile" ;;
      *Tiltfile*)                        echo "starlark" ;;
      *.http)                            echo "http" ;;
      *.sh|*.bash)                       echo "bash" ;;
      *)                                 echo "text" ;;
    esac
  }

  git ls-files --cached --others --exclude-standard \
    | grep -E '\.(cs|csproj|ts|tsx|js|jsx|css|json|yaml|yml|tf|tfvars|http|proto|sql)$|Dockerfile|Makefile|Tiltfile' \
    | grep -vE 'package-lock.json|pnpm-lock.yaml|yarn.lock' \
    | while read -r file; do
        if [ -f "$file" ]; then
          ext=$(get_syntax "$file")
          printf "\n### File: %s\n\`\`\`%s\n" "$file" "$ext"
          cat "$file"
          printf "\n\`\`\`\n"
        fi
      done
} | pbcopy && echo "All relevant source files copied to clipboard!"
