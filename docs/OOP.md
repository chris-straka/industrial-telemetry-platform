# Prototypal Inheritance vs Classical Inheritance

Used in JS and Lua, where methods/properties are inherited via reusing prototype objects.

# Abstract Classes vs Interfaces

Traditionally, abstract classes could include abstract (overridable) and concrete methods/properties.
They also support single inheritance only.

# Cohesion, coupling and data hiding

Cohesion is bundling related things together so that a class does one thing really well.

Coupling is how linked one piece of code is to another (higher maintenance when things change)

Data hiding is about setting up guard rails for accidental missuse (creates loose coupling)

Low coupling, high cohesion is the real goal behind most SOLID rules.
Can you point to a class where everything inside belongs together, and it talks to few others through narrow interfaces?

# Tell, Don't Ask

Don't pull data out of an object to make decisions elsewhere. Tell the object to do it.

```cs
order.ship() // GOOD

if (order.status == READY) { // BAD
    shipper.ship(order);
}
```

# Law of Demeter

Demeter: only talk to immediate friends,

```cs
a.getB().getC().doX(); // AVOID
```

# Immutability

OOP isn't just mutable entities.
Immutable value objects (Money, DateRange) simplify reasoning, especially in concurrent code.
Knowing when an object needs identity vs just value is huge.

# Four pillars (abstraction, inheritance, polymorphism and encapsulation)

Inheritance creates tight coupling, an ‘is-a’ relationship for code reuse.
Abstraction hides implementation details, exposing only the necessary interface (simplifies)
Encapsulation bundles data & methods while hiding internal state to protect object integrity.
Polymorphism allows different objects to be treated uniformly through a common interface

# Inheritance

The fragile base class problem happens when many subclasses depend on a parent's implementation, not just its interface.
Changing the parent, even internally, can ripple and break subclasses.
Because of that tight coupling, you often have to edit the base class to add new subclasses, which violates OCP.

# Encapsulation

Encapsulation is about protecting invariants, i.e. a rule that must always be true.
You dont want outsiders to break its rules.

# Polymorphism

"Same name, many forms"

There's three types

1. Subtype (inheritance/overriding)
2. Parametric (generics)
3. Ad-hoc (overloading)

Subtype = runtime dispatch on receiver (this.foo())
The method you call at runtime depends on the actual object.

```cs
Animal a = new Cat();
a.noise() // could be meow, squeek, bark
```

Parametric = same type parameter, many type arguments.

```java
List<Integer> ints = new ArrayList<>();
List<String> names = new ArrayList<>();
```

Ad-hoc = compile-time dispatch on parameters.
Same name for the method, but it behaves differently depending on args passed at compile time.

```java
int foo(int a) {}
int foo(String a) {}
foo(30);
```

# SOLID

## S: SRP “Single Responsibility Principle”

You should only have one reason to change a class.

This does not mean a class should only have a single method/action/behavior.

```cs
class Tractor {
	plow() {}
	seed() {}
	sendEmailReport() {} // SRP violation
}
```

# O: OCP “Open Close Principle”

Classes should be open to extension, closed to modification

OCP violation (you’re modifying the class)

```cs
class PaymentProcessor {
    void process(String type) {
        if (type.equals("credit")) {}
        else if (type.equals("paypal")) {} // OCP violation
    }
}
```

OCP Fix (you should make your class extensible through interfaces or HOF)

```cs
interface PaymentMethod { void process(); }

class CreditCardPayment implements PaymentMethod { public void process() { ... } }

class PayPalPayment implements PaymentMethod { public void process() { ... } }

class PaymentProcessor {
    void process(PaymentMethod method) {
        method.process();
    }
}
```

Can also be done with HOF or HOO "Higher Order Objects" or HOL "Higher Order Lambdas".
A lambda is a concisee way to represent a function (in Java, it's an obj with 1 method).
HOO is basically the strategy pattern with objects.

Switch statements that change the core logic of the class are usually a smell.

# L: LSP "Liskov Substitution Principle"

You should be able to use children in place of parents.
Be careful when you override a parent method that you don’t break the parent's contract.

```cs
class Rectangle {
    void setWidth(int w) { ... }
    void setHeight(int h) { ... }
}

class Square : Rectangle {
    // LSP VIOLATION
    void setWidth(int w) {
        super.setWidth(w);
        super.setHeight(w);
    }
}

Rectangle r = new Square();
r.setHeight(10);
r.setWidth(5);
r // 5, 5 (not 5, 10)
```

Preconditions → what must be true before calling a method (inputs, state)
Postconditions → what the method guarantees after it runs (outputs, effects)

Regarding virtual methods, the subclass must...
NOT require more (no stricter preconditions)
NOT promise less (no weaker postconditions)

```cs
class Bird {
    void fly() {}
}

class Penguin : Bird {
    void fly() {} // LSP violation
}

// Don't force a bad inheritance by changing fly() to flapWings()
// Do this instead
class FlyingBird {
    void fly() {}
}
```

# I: ISP "Interface Segregation Principle"

Clients shouldn’t be forced to depend on methods they don’t use.

```java
interface Machine {
    void print();
    void scan();
    void fax();
}
```

```java
class BasicPrinter implements Machine {
    void print() { ... }
    void scan() { throw new UnsupportedOperationException(); } // ❌
    void fax() { throw new UnsupportedOperationException(); }  // ❌
}
```

```java
interface Printer {
    void print();
}

interface Scanner {
    void scan();
}
```

# D: DIP "Dependency Inversion Principle"

High-level modules (business logic) should not depend on low-level modules (concrete implementations).
Both should instead depend on abstractions (interfaces).

This is commonly achieved with DI.
It's when a parent relies on an interface and then you pass an obj that obeys that interface.

# GRASP

General Responsibility Assignment Software Patterns

Information Expert - Put the method on the class that has the data.

Creator - B should create A if it's aggregating A, has info to create A

Controller - one entry point for a use case, like OrderController.placeOrder(), keeps UI out of domain.

Pure Fabrication - invent a class that doesn't exist in the domain just to keep design clean, like `PriceCalculator`.

Protected Variations - hide likely changes behind an interface, same idea as OCP.

Indirection - put a mediator between two things so they don't depend directly.
