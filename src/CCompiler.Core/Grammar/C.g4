grammar C;

primaryExpression
    : Identifier
    | Constant
    | StringLiteral+
    | '(' expression ')'
    ;

postfixExpression
    : (primaryExpression | '(' typeName ')' '{' initializerList ','? '}') (
        '[' expression ']'
        | '(' argumentExpressionList? ')'
        | ('.' | '->') Identifier
        | '++'
        | '--'
    )*
    ;

argumentExpressionList
    : assignmentExpression (',' assignmentExpression)*
    ;

unaryExpression
    : ('++' | '--' | 'sizeof')* (
        postfixExpression
        | unaryOperator castExpression
        | 'sizeof' '(' typeName ')'
    )
    ;

unaryOperator
    : '&'
    | '*'
    | '+'
    | '-'
    | '~'
    | '!'
    ;

castExpression
    : '(' typeName ')' castExpression
    | unaryExpression
    | DigitSequence // for
    ;

multiplicativeExpression
    : castExpression (('*' | '/' | '%') castExpression)*
    ;

additiveExpression
    : multiplicativeExpression (('+' | '-') multiplicativeExpression)*
    ;

shiftExpression
    : additiveExpression (('<<' | '>>') additiveExpression)*
    ;

relationalExpression
    : shiftExpression (('<' | '>' | '<=' | '>=') shiftExpression)*
    ;

equalityExpression
    : relationalExpression (('==' | '!=') relationalExpression)*
    ;

andExpression
    : equalityExpression ('&' equalityExpression)*
    ;

exclusiveOrExpression
    : andExpression ('^' andExpression)*
    ;

inclusiveOrExpression
    : exclusiveOrExpression ('|' exclusiveOrExpression)*
    ;

logicalAndExpression
    : inclusiveOrExpression ('&&' inclusiveOrExpression)*
    ;

logicalOrExpression
    : logicalAndExpression ('||' logicalAndExpression)*
    ;

conditionalExpression
    : logicalOrExpression ('?' expression ':' conditionalExpression)?
    ;

assignmentExpression
    : conditionalExpression
    | unaryExpression assignmentOperator assignmentExpression
    | DigitSequence // for
    ;

assignmentOperator
    : '='
    | '*='
    | '/='
    | '%='
    | '+='
    | '-='
    | '<<='
    | '>>='
    | '&='
    | '^='
    | '|='
    ;

expression
    : assignmentExpression (',' assignmentExpression)*
    ;

constantExpression
    : conditionalExpression
    ;

declaration
    : declarationSpecifiers initDeclaratorList? ';'
    | staticAssertDeclaration
    ;

declarationSpecifiers
    : declarationSpecifier+
    ;

declarationSpecifiers2
    : declarationSpecifier+
    ;

declarationSpecifier
    : storageClassSpecifier
    | typeSpecifier
    | typeQualifier
    | 'inline'
    ;

initDeclaratorList
    : initDeclarator (',' initDeclarator)*
    ;

initDeclarator
    : declarator ('=' initializer)?
    ;

storageClassSpecifier
    : 'typedef'
    | 'extern'
    | 'static'
    | 'auto'
    | 'register'
    ;

typeSpecifier
    : 'void'
    | 'char'
    | 'short'
    | 'int'
    | 'long'
    | 'float'
    | 'double'
    | 'signed'
    | 'unsigned'
    | '_Bool'
    | structOrUnionSpecifier
    | enumSpecifier
    | typedefName
    ;

structOrUnionSpecifier
    : structOrUnion Identifier? '{' structDeclarationList '}'
    | structOrUnion Identifier
    ;

structOrUnion
    : 'struct'
    | 'union'
    ;

structDeclarationList
    : structDeclaration+
    ;

structDeclaration // The first two rules have priority order and cannot be simplified to one expression.
    : specifierQualifierList structDeclaratorList ';'
    | specifierQualifierList ';'
    | staticAssertDeclaration
    ;

specifierQualifierList
    : (typeSpecifier | typeQualifier) specifierQualifierList?
    ;

structDeclaratorList
    : structDeclarator (',' structDeclarator)*
    ;

structDeclarator
    : declarator
    | declarator? ':' constantExpression
    ;

enumSpecifier
    : 'enum' Identifier? '{' enumeratorList ','? '}'
    | 'enum' Identifier
    ;

enumeratorList
    : enumerator (',' enumerator)*
    ;

enumerator
    : Identifier ('=' constantExpression)?
    ;

typeQualifier
    : 'const'
    | 'restrict'
    | 'volatile'
    ;

declarator
    : pointer? directDeclarator
    ;

directDeclarator
    : Identifier
    | '(' declarator ')'
    | directDeclarator '[' typeQualifierList? assignmentExpression? ']'
    | directDeclarator '(' parameterTypeList ')'
    | directDeclarator '(' identifierList? ')'
    ;

pointer
    : ('*' typeQualifierList?)+
    ;

typeQualifierList
    : typeQualifier+
    ;

parameterTypeList
    : parameterList (',' '...')?
    ;

parameterList
    : parameterDeclaration (',' parameterDeclaration)*
    ;

parameterDeclaration
    : declarationSpecifiers declarator
    | declarationSpecifiers2 abstractDeclarator?
    ;

identifierList
    : Identifier (',' Identifier)*
    ;

typeName
    : specifierQualifierList abstractDeclarator?
    ;

abstractDeclarator
    : pointer
    | pointer? directAbstractDeclarator
    ;

directAbstractDeclarator
    : '(' abstractDeclarator ')'
    | '[' typeQualifierList? assignmentExpression? ']'
    | '(' parameterTypeList? ')'
    | directAbstractDeclarator '[' typeQualifierList? assignmentExpression? ']'
    | directAbstractDeclarator '(' parameterTypeList? ')'
    ;

typedefName
    : Identifier
    ;

initializer
    : assignmentExpression
    | '{' initializerList ','? '}'
    ;

initializerList
    : designation? initializer (',' designation? initializer)*
    ;

designation
    : designatorList '='
    ;

designatorList
    : designator+
    ;

designator
    : '[' constantExpression ']'
    | '.' Identifier
    ;

staticAssertDeclaration
    : '_Static_assert' '(' constantExpression ',' StringLiteral+ ')' ';'
    ;

statement
    : labeledStatement
    | compoundStatement
    | expressionStatement
    | selectionStatement
    | iterationStatement
    | jumpStatement
    ;

labeledStatement
    : Identifier ':' statement?
    | 'case' constantExpression ':' statement
    | 'default' ':' statement
    ;

compoundStatement
    : '{' blockItemList? '}'
    ;

blockItemList
    : blockItem+
    ;

blockItem
    : statement
    | declaration
    ;

expressionStatement
    : expression? ';'
    ;

selectionStatement
    : 'if' '(' expression ')' statement ('else' statement)?
    | 'switch' '(' expression ')' statement
    ;

iterationStatement
    : While '(' expression ')' statement
    | Do statement While '(' expression ')' ';'
    | For '(' forCondition ')' statement
    ;

forCondition
    : (forDeclaration | expression?) ';' forExpression? ';' forExpression?
    ;

forDeclaration
    : declarationSpecifiers initDeclaratorList?
    ;

forExpression
    : assignmentExpression (',' assignmentExpression)*
    ;

jumpStatement
    : (
        'goto' Identifier
        | 'continue'
        | 'break'
        | 'return' expression?
    ) ';'
    ;

compilationUnit
    : translationUnit? EOF
    ;

translationUnit
    : externalDeclaration+
    ;

externalDeclaration
    : functionDefinition
    | declaration
    | ';' // stray ;
    ;

functionDefinition
    : declarationSpecifiers? declarator declarationList? compoundStatement
    ;

declarationList
    : declaration+
    ;

Do
    : 'do'
    ;

For
    : 'for'
    ;

While
    : 'while'
    ;

Identifier
    : Nondigit (Nondigit | Digit)*
    ;

Constant
    : IntegerConstant
    | FloatingConstant
    | CharacterConstant
    ;

DigitSequence
    : Digit+
    ;

StringLiteral
    : '"' SChar* '"'
    ;

Directive
    : '#' ~[\r\n]* -> channel(HIDDEN)
    ;

Whitespace
    : [ \t\r\n]+ -> channel(HIDDEN)
    ;

BlockComment
    : '/*' .*? '*/' -> channel(HIDDEN)
    ;

LineComment
    : '//' ~[\r\n]* -> channel(HIDDEN)
    ;

fragment Nondigit
    : [a-zA-Z_]
    ;

fragment Digit
    : [0-9]
    ;

fragment HexDigit
    : [0-9a-fA-F]
    ;

fragment IntegerConstant
    : ('0' [xX] HexDigit+ | '0' [bB] [01]+ | Digit+) [uUlL]*
    ;

fragment FloatingConstant
    : (Digit* '.' Digit+ | Digit+ '.') ([eE] [+-]? Digit+)? [flFL]?
    | Digit+ [eE] [+-]? Digit+ [flFL]?
    ;

fragment CharacterConstant
    : '\'' CChar+ '\''
    ;

fragment CChar
    : ~['\\\r\n]
    | EscapeSequence
    ;

fragment SChar
    : ~["\\\r\n]
    | EscapeSequence
    | '\\\r'? '\n'
    ;

fragment EscapeSequence
    : '\\' ['"?abfnrtv\\]
    | '\\' [0-7] [0-7]? [0-7]?
    | '\\x' HexDigit+
    ;
